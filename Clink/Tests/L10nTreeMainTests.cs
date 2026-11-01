using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Clink.Capture;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Localization slice S1 (main window and settings area): under the pseudo-locale (L.Scope("qps-ploc")) the main window, the hotkey list
/// editor and the texts the code sets (Copium card, estimates, import banner and notices) must all come from the catalog; and every key
/// the slice's files ask for must exist in the English catalog (a scan of the sources, so a branch no test reaches is covered too).
/// Windows are built and measured off screen, never shown.
/// </summary>
internal static class L10nTreeMainTests
{
    // ------------------------------------------------------------------ helpers

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static object? Call(object target, string name, params object?[] args)
    {
        var m = target.GetType().GetMethod(name, Any) ?? throw new TestFailure("no method " + name + " on " + target.GetType().Name);
        try { return m.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw new TestFailure(name + " threw " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message); }
    }

    /// <summary>Text that needs no translation although it has letters: a resolution name ("1080p"), a codec name ("AV1").</summary>
    private static bool Untranslatable(string text) =>
        Regex.IsMatch(text.Trim(), @"^(\d{3,4}p|AV1|PrintScreen)$");   // codec / resolution names, and the key cap of a hotkey (hotkey names stay English in v0.4.5)

    private static List<string> Unlocalized(DependencyObject root) =>
        L10nTreeHarness.Unlocalized(root, Untranslatable).ToList();

    private static IEnumerable<ItemsControl> ItemsControls(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        var seen = new HashSet<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            if (node is ItemsControl ic) yield return ic;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) stack.Push(d);
        }
    }

    /// <summary>
    /// The labels of the choices (combo entries that are objects, not strings: the tree walk cannot see them) as TextBlocks, so the same
    /// check applies. The audio pickers also list devices and running apps by their own names (user data), so only their fixed entries
    /// (headers, "Default ...", "Auto ...") are taken.
    /// </summary>
    private static StackPanel ChoiceLabels(DependencyObject root)
    {
        var panel = new StackPanel();
        string[] audio = { "GameCombo", "MicCombo", "VoiceCombo", "AllDeviceCombo", "RunningAppsCombo" };
        foreach (var ic in ItemsControls(root))
        {
            bool devices = ic is FrameworkElement { Name: { } n } && audio.Contains(n);
            foreach (object item in ic.Items)
            {
                if (item is string or DependencyObject or null) continue;   // elements (ComboBoxItem, TabItem) are walked as elements
                if (devices && item is Choice c && !(c.Header || c.Value is "" or "auto")) continue;
                panel.Children.Add(new TextBlock { Text = item.ToString() ?? "" });
            }
        }
        return panel;
    }

    /// <summary>How many TextBlocks of the (logical) tree hold a pseudo-localized text: proof the tree was really read.</summary>
    private static int FramedTexts(DependencyObject root)
    {
        int n = 0;
        var stack = new Stack<DependencyObject>();
        var seen = new HashSet<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            if (node is TextBlock tb && PseudoLocale.IsPseudo(tb.Text)) n++;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) stack.Push(d);
        }
        return n;
    }

    private static void None(IReadOnlyCollection<string> found, string what) =>
        Check.Equal(0, found.Count, what + ": " + string.Join(" | ", found.Take(12)));

    private static AppController NewApp()
    {
        var settings = Settings.Load();   // the runner's scratch folder
        settings.CaptureBorderNoticeDismissed = true;   // so a border state is only a line on the Capture tab, not a toast window
        return new AppController(settings, new Updater(settings));
    }

    // ------------------------------------------------------------------ the main window

    [UnitTest]
    public static void The_main_window_has_no_text_outside_the_catalog_in_any_tab_or_state()
    {
        using var app = NewApp();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var w = new MainWindow();
        Call(w, "Populate");   // what the Loaded handler does: combos, labels, estimates, toggles, the Copium card

        // every tab selected in turn (each one's content is laid out and its templates are made), then the states that set text from code
        Check.True(w.Tabs.Items.OfType<TabItem>().Count() >= 6, "the settings tabs are there");
        Check.True(FramedTexts(w) > 60, "the walk reaches the catalog texts of the window: " + FramedTexts(w));
        var found = new List<string>();
        foreach (var tab in w.Tabs.Items.OfType<TabItem>())
        {
            w.Tabs.SelectedItem = tab;
            found.AddRange(Unlocalized(w));
        }
        found.AddRange(Unlocalized(ChoiceLabels(w)));
        None(found.Distinct().ToList(), "the main window after Populate");

        // the Copium card: every panel and every failure line
        w.Tabs.SelectedItem = w.AppTab;
        var states = new List<SignInState>
        {
            new(SignInStatus.SignedOut),
            new(SignInStatus.SignedOut, HasToken: true),
            new(SignInStatus.Starting),
            new(SignInStatus.Starting, HasToken: true, LinkedViaConnect: true),
            new(SignInStatus.Waiting, Code: "ABCD-1234"),
            new(SignInStatus.Waiting, Code: "ABCD-1234", Retrying: true),
            new(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "Jason", Plus: true, Verified: true),
            new(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Verified: false, AccountCheckFailed: true),
            new(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "Jason", Verified: true, RemoteCleanupName: "Clink-PC", RemoteCleanupGuess: true),
            new(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "Jason", Verified: true, RemoteCleanupName: "Clink-PC"),
        };
        foreach (SignInFailure f in Enum.GetValues<SignInFailure>())
            states.Add(new SignInState(SignInStatus.Failed, Failure: f));
        var card = new List<string>();
        foreach (var st in states)
        {
            Call(w, "RenderCopiumCard", st);
            card.AddRange(Unlocalized(w));
        }
        None(card.Distinct().ToList(), "the Copium card states");

        // the Capture tab: the yellow-border line for each outcome, the estimates for each size target and length
        w.Tabs.SelectedItem = w.CaptureTab;
        var capture = new List<string>();
        foreach (CaptureBorderState b in Enum.GetValues<CaptureBorderState>())
        {
            Call(app, "OnCaptureBorder", b, "detail");
            capture.AddRange(Unlocalized(w));
        }
        foreach (string target in Capabilities.SizeTargets.Select(t => t.Id))
            foreach (int seconds in new[] { 45, 600, 1800, 3600 })
            {
                app.Settings.SizeTarget = target;
                Call(w, "UpdateEstimates", (int?)seconds);
                capture.AddRange(Unlocalized(w));
            }
        None(capture.Distinct().ToList(), "the Capture tab states");

        // the App tab banners
        var copy = new ForeignSettings("Contoso.Notes_pzs8sxrjxfjjc", @"C:\x", new DateTime(2026, 10, 21, 18, 32, 5, DateTimeKind.Utc), new[] { "video quality", "hotkeys" });
        w.ImportText.Text = MainWindow.ImportBannerText(copy);
        w.RedirectText.Text = AppDataRedirect.NoticeFor("Notes");
        None(Unlocalized(w), "the import and redirect banners");

        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void The_hotkey_list_editor_has_no_text_outside_the_catalog()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var ed = new HotkeyListEditor();
        var actions = new List<HotkeyAction>
        {
            new() { Keys = new HotkeyBinding { Vk = 0x78 }, Action = "clip", Seconds = 0 },                                  // the main hotkey
            new() { Keys = new HotkeyBinding { Vk = 0x79, Ctrl = true }, Action = "clip", Seconds = 15 },
            new() { Keys = new HotkeyBinding { Vk = 0x7A, Shift = true }, Action = "clip", Seconds = 0 },                    // saves the full replay
            new() { Keys = new HotkeyBinding { Vk = 0x7B }, Action = "clip", Seconds = 900 },                                 // longer than the replay: capped
            new() { Keys = new HotkeyBinding { Vk = 0 }, Action = "pause" },
            new() { Keys = new HotkeyBinding { Vk = 0x2C }, Action = "screenshot" },
        };
        ed.Load(actions, 120);
        var found = new List<string>();
        found.AddRange(Unlocalized(ed));
        found.AddRange(Unlocalized(ChoiceLabels(ed)));
        None(found.Distinct().ToList(), "the hotkey editor");
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    // ------------------------------------------------------------------ code-set texts: pseudo marker under qps, today's English under en

    /// <summary>Strict: the text carries the pseudo marker AND nothing translatable is left once every pseudo frame is cut out (a hard-coded English tail fails).</summary>
    private static void Marked(string? text, string what)
    {
        Check.True(PseudoLocale.IsPseudo(text), what + " carries the pseudo marker: " + text);
        Check.True(L10nTreeHarness.IsAllowed(L10nTreeHarness.StripFrames(text!)), what + " has text outside the catalog frames: " + text);
    }

    [UnitTest]
    public static void The_Copium_card_texts_come_from_the_catalog_and_read_as_before_in_English()
    {
        L.ClearMissing();
        string en = CopiumCardModel.SignInLabel;
        Check.Equal("Sign in with Copium", en);
        Check.Equal("Signed in as \u2068Jason\u2069 · Copium+", CopiumCardModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "Jason", Plus: true, Verified: true), true, false, false, "PC", null).SignedInText);
        Check.Equal("Signed in", CopiumCardModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Verified: true), true, false, false, "PC", null).SignedInText);
        Check.Equal("Clink couldn't remove its connection from Copium. Remove “\u2068PC\u2069” on copium.pics under ShareX & API.",
            CopiumCardModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Verified: true, RemoteCleanupName: "PC"), true, false, false, "PC", null).CleanupText);

        using var scope = L.Scope("qps-ploc");
        Marked(CopiumCardModel.SignInLabel, "SignInLabel");
        Marked(CopiumCardModel.NoTokenShareHint, "NoTokenShareHint");
        Marked(CopiumCardModel.DevNeedsTokenReason, "DevNeedsTokenReason");
        var signedIn = CopiumCardModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, Username: "Jason", Plus: true, Verified: true), true, false, false, "PC", null);
        Marked(signedIn.SignedInText, "SignedInText");
        Check.Contains(signedIn.SignedInText, "Jason");
        Marked(signedIn.PlusNote, "PlusNote");
        Marked(CopiumCardModel.From(new SignInState(SignInStatus.Waiting, Code: "X"), true, false, false, "PC", null).StatusText, "waiting status");
        Marked(CopiumCardModel.From(new SignInState(SignInStatus.SignedIn, HasToken: true, LinkedViaConnect: true, RemoteCleanupName: "PC", RemoteCleanupGuess: true), true, false, false, "PC", null).CleanupText, "cleanup text");
        foreach (SignInFailure f in Enum.GetValues<SignInFailure>())
            Marked(CopiumCardModel.FailureLine(f), "failure " + f);
        foreach (SignOutResult r in new[] { SignOutResult.Revoked, SignOutResult.NotSupportedDisconnectOnWebsite, SignOutResult.FailedButSignedOutLocally, SignOutResult.Manual })
            Marked(CopiumCardModel.SignOutText(r), "sign out " + r);
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void Size_and_length_texts_come_from_the_catalog_and_read_as_before_in_English()
    {
        // English: the sentences are what they always were
        L.ClearMissing();
        Check.Equal("9.5 MB", Capabilities.FormatBytes(9_500_000));
        Check.Equal("1:30", Capabilities.FormatDuration(90));
        Check.Equal("1 h", Capabilities.FormatDuration(3600));
        Check.Equal("0:42", Capabilities.FormatClipLength(42));
        Check.Equal("≈ " + Capabilities.FormatBytes(Capabilities.EstimateBytes(10, 3, 60)) + " per minute · ≈ " + Capabilities.FormatBytes(Capabilities.EstimateBytes(10, 3, 3600)) + " per hour",
            Capabilities.DescribeEstimate(10, 3, 3600));
        Check.Equal("≈ " + Capabilities.FormatBytes(Capabilities.EstimateBytes(10, 3, 60)) + " per minute · ≈ " + Capabilities.FormatBytes(Capabilities.EstimateBytes(10, 3, 120)) + " per 2:00 clip",
            Capabilities.DescribeEstimate(10, 3, 120));
        Check.Equal("Copium's free limit (200 MB / 10 min)", Capabilities.CopiumLimits(false).Label);
        var s = new Settings { SizeTarget = "copium200", BitrateMbps = 16, KeyframeSeconds = 1 };
        var (_, note, warning) = Capabilities.ResolveBitrateChecked(s, 3, 600);
        Check.Equal("lowered to 1.9 Mbps so a 10:00 clip fits 200 MB" + Capabilities.CopiumLengthNote + " (a 10:00 replay saves a hair over 10 min)", note);
        Check.True(warning, "the length limit is a warning");
        (_, note, warning) = Capabilities.ResolveBitrateChecked(s, 3, 45);
        Check.Equal("a 45 s clip at 16 Mbps fits 200 MB", note);
        Check.False(warning);
        (_, note, warning) = Capabilities.ResolveBitrateChecked(new Settings { SizeTarget = "discord10" }, 3, 3600);
        Check.Contains(note, "does not fit in 20 MB even at 0.5 Mbps; shorten the replay length");
        Check.True(warning);
        Check.Equal("H.264", Capabilities.Codecs[0].Name);
        Check.Equal("plays everywhere, incl. Discord inline and every browser; biggest files.", Capabilities.Codecs[0].Description);

        // pseudo-locale: a translation would carry the marker, wherever the sentence is built
        using var scope = L.Scope("qps-ploc");
        Marked(Capabilities.FormatBytes(9_500_000), "FormatBytes");
        Marked(Capabilities.FormatDuration(45), "FormatDuration");
        Marked(Capabilities.DescribeEstimate(10, 3, 3600), "estimate per hour");
        Marked(Capabilities.DescribeEstimate(10, 3, 120), "estimate per clip");
        Marked(Capabilities.DescribeRing(10, 120).text, "RAM line");
        Marked(Capabilities.DescribeRing(2000, 3600).text, "RAM line over half of the RAM");   // 900 GB: more than half of any machine's
        Marked(Capabilities.CopiumLimits(true).Label, "Plus limit");
        foreach (var t in Capabilities.SizeTargets) Marked(t.Name, "size target " + t.Id);
        foreach (var c in Capabilities.Codecs) { Marked(c.Description, "codec " + c.Id); if (!c.Supported) Marked(c.Reason, "codec reason " + c.Id); }
        foreach (var (target, seconds) in new[] { ("none", 45), ("copium200", 45), ("copium200", 600), ("copium200", 1800), ("copiumplus", 3600), ("copiumplus", 7200), ("discord10", 3600), ("discord50", 45) })
        {
            var (_, n, _) = Capabilities.ResolveBitrateChecked(new Settings { SizeTarget = target, BitrateMbps = 16, KeyframeSeconds = 1 }, 3, Math.Min(3600, seconds));
            if (target != "none") Marked(n, "note " + target + " " + seconds);
        }
        foreach (var t in new[] { "dark", "light" }) Marked(ThemeManager.Themes.First(x => x.Id == t).Label, "theme " + t);
        foreach (var a in ThemeManager.Accents) Marked(a.Label, "accent " + a.Id);
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void Context_labels_read_without_their_context_in_English_and_are_in_the_catalog()
    {
        L.ClearMissing();
        // the context before the '|' never shows: these are the words the tabs, combos and swatches always had
        Check.Equal("None", Capabilities.SizeTargets[0].Name);
        Check.Equal(new[] { "Dark", "Light" }, ThemeManager.Themes.Select(t => t.Label).ToArray());
        Check.Equal(new[] { "Spark Blue", "Blue", "Violet", "Pink", "Orange", "Green" }, ThemeManager.Accents.Select(a => a.Label).ToArray());
        foreach (var key in new[] { "accent|Blue", "theme|Light", "position|Top left", "position|Bottom", "size-target|None", "resolution|Native", "hotkey-action|Clip", "verb|Preview", "verb|Test", "verb|Update", "replay-length|ideal", "tab|Capture", "track|Voice chat" })
            Check.Equal(key.Substring(key.IndexOf('|') + 1), L.T(key));
        // every context key has a pseudo form that is fully framed
        using (L.Scope("qps-ploc"))
        {
            foreach (var t in Capabilities.SizeTargets.Take(1)) Marked(t.Name, "size target none");
            Marked(L.T("verb|Update"), "verb|Update");
        }
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void Only_the_Copium_targets_have_a_length_limit_and_the_catalog_keys_name_it()
    {
        // the catalog keys "Copium (200 MB, 10 min)", "Copium Plus (2 GB, 60 min)", the "10 min" / "60 min" sentences and "10:00" / "60:00" are written out in main.json: they go stale if these change
        var limited = Capabilities.SizeTargets.Where(t => t.MaxSeconds > 0).Select(t => t.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check.Equal(new[] { "copium200", "copiumplus" }, limited);
        Check.Equal(600, Capabilities.CopiumFreeSeconds);
        Check.Equal(3600, Capabilities.CopiumPlusSeconds);
        Check.Equal(600, Capabilities.SizeTargetFor("copium200").MaxSeconds);
        Check.Equal(3600, Capabilities.SizeTargetFor("copiumplus").MaxSeconds);
    }

    [UnitTest]
    public static void Footer_status_monitor_and_update_lines_come_from_the_catalog_and_read_as_before_in_English()
    {
        L.ClearMissing();
        var monitor = new Clink.Native.MonitorInfo(0, @"\\.\DISPLAY1", "AW2726DM", 0, 0, 2560, 1440, true, false, 80f, 0f, "NVIDIA");
        var secondary = monitor with { IsPrimary = false, FriendlyName = "" };

        // English: what the window always said
        Check.Equal("Overwatch · NVIDIA H.264 · buffer 119s · 3 dropped", MainWindow.FooterLine("Overwatch", "NVIDIA H.264 Encoder MFT", 119.4, 3));
        Check.Equal("NVIDIA H.264 · buffer 0s · 0 dropped", MainWindow.FooterLine(null, "NVIDIA H.264", 0, 0));
        Check.Equal("Recording paused", MainWindow.IdleFooterLine(RecordingState.Paused, null, "x"));
        Check.Equal("boom", MainWindow.IdleFooterLine(RecordingState.Error, "boom", "x"));
        Check.Equal("Error", MainWindow.IdleFooterLine(RecordingState.Error, null, "x"));
        Check.Equal("Detected: Overwatch (fullscreen window)", MainWindow.ModeStatusText(true, "Overwatch", "fullscreen"));
        Check.Equal("Detected: Overwatch", MainWindow.ModeStatusText(true, "Overwatch", null));
        Check.Equal("Recording whenever Clink runs.", MainWindow.ModeStatusText(false, null, null));
        Check.Equal("Recording whenever Clink runs · game: Overwatch", MainWindow.ModeStatusText(false, "Overwatch", "library"));
        Check.Equal("AW2726DM  ·  2560×1440  ·  primary", MainWindow.MonitorLabelText(monitor));
        Check.Equal("DISPLAY1  ·  2560×1440", MainWindow.MonitorLabelText(secondary));
        Check.Equal("No monitor", MainWindow.MonitorLabelText(null));
        Check.Equal("Update to v1.2.3", MainWindow.UpdateButtonText("1.2.3", true));
        Check.Equal("Update", MainWindow.UpdateButtonText("", false));
        Check.Equal("disk full", MainWindow.UpdateProblemLine("disk full", "other", true, false, false));
        Check.Equal("Last check failed: offline", MainWindow.UpdateProblemLine(null, "offline", true, false, false));
        Check.Equal("Update problem: bad signature", MainWindow.UpdateProblemLine(null, "bad signature", false, false, false));
        Check.Null(MainWindow.UpdateProblemLine(null, null, false, false, false));
        Check.Null(MainWindow.UpdateProblemLine(null, Updater.DevModeNote.ToUpperInvariant(), true, true, lastErrorIsDevMode: true));   // dev mode: said by the version line, not as a problem (told by the flag, never by the words)
        Check.NotNull(MainWindow.UpdateProblemLine(null, Updater.DevModeNote, true, true, lastErrorIsDevMode: false));   // the same words without the flag are an ordinary problem
        Check.Equal("Last check failed: " + Updater.DevModeNote, MainWindow.UpdateProblemLine(null, Updater.DevModeNote, true, false, false));
        None(L.Missing.ToList(), "keys asked for and not in the English catalog (English)");

        // pseudo-locale: every builder is in the catalog, nothing else is left in the sentence
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        Marked(MainWindow.FooterLine("Overwatch", "NVIDIA H.264 Encoder MFT", 119.4, 3), "footer with a game");
        Marked(MainWindow.FooterLine(null, "NVIDIA H.264", 5, 0), "footer without a game");
        foreach (RecordingState s in Enum.GetValues<RecordingState>())
            if (s is RecordingState.Error or RecordingState.WaitingForGame or RecordingState.Paused) Marked(MainWindow.IdleFooterLine(s, null, "x"), "idle footer " + s);
        Marked(MainWindow.IdleFooterLine(RecordingState.Error, null, "x"), "idle footer error");   // an engine error text is the engine's own (shown as it is)
        foreach (string? source in new string?[] { "fullscreen", "library", "custom", null })
            foreach (string? game in new[] { "Overwatch", null })
            {
                Marked(MainWindow.ModeStatusText(true, game, source), "mode game-only " + game + "/" + source);
                Marked(MainWindow.ModeStatusText(false, game, source), "mode always " + game + "/" + source);
            }
        Marked(MainWindow.MonitorLabelText(monitor), "monitor primary");
        Marked(MainWindow.MonitorLabelText(secondary), "monitor secondary");
        Marked(MainWindow.MonitorLabelText(null), "no monitor");
        Marked(MainWindow.UpdateButtonText("1.2.3", true), "update to version");
        Marked(MainWindow.UpdateButtonText("", false), "update");
        Marked(MainWindow.UpdateProblemLine(null, "offline", true, false, false), "check failed");
        Marked(MainWindow.UpdateProblemLine(null, "bad signature", false, false, false), "update problem");
        None(L.Missing.ToList(), "keys asked for and not in the English catalog (pseudo)");
    }

    [UnitTest]
    public static void The_hotkey_capture_hints_come_from_the_catalog_and_read_as_before_in_English()
    {
        L.ClearMissing();
        var keys = new HotkeyBinding { Vk = 0x79, Ctrl = true };
        Check.Equal("Ctrl+F10 is already used by another hotkey. Press other keys, or Esc.", HotkeyListEditor.DuplicateKeysHint(keys).Replace("\u2066", "").Replace("\u2069", "").Replace("\u2068", ""));
        Check.Equal("Letters, digits and editing keys need Ctrl or Alt; F-keys, PrintScreen, ScrollLock and Pause work alone.", HotkeyListEditor.BareKeyHint());
        using var scope = L.Scope("qps-ploc");
        Marked(HotkeyListEditor.DuplicateKeysHint(keys), "duplicate keys hint");
        Marked(HotkeyListEditor.BareKeyHint(), "bare key hint");
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void The_codec_lines_for_an_unsupported_codec_are_whole_sentences()
    {
        // the one reason today (Capabilities.NotSupportedReason): both lines are catalog sentences, not "{codec} is " + a predicate
        var en = L.Catalogs.English.Entries;
        Check.True(en.ContainsKey("{name} — not supported by this GPU"), "combo entry");
        Check.True(en.ContainsKey("{codec} is not supported by this GPU; recording uses {fallback} instead. {codec} – {description}"), "description line");
        Check.False(en.ContainsKey("{name} — {reason}"), "the fragment key is gone");
        Check.False(en.ContainsKey("{codec} is {reason}; recording uses {fallback} instead. {codec} – {description}"), "the fragment key is gone");
    }

    [UnitTest]
    public static void The_warning_flag_of_a_size_note_does_not_depend_on_its_words()
    {
        // under the pseudo-locale the words differ, the flag does not
        var s = new Settings { SizeTarget = "copium200", BitrateMbps = 16, KeyframeSeconds = 1 };
        bool[] english = { Capabilities.ResolveBitrateChecked(s, 3, 45).warning, Capabilities.ResolveBitrateChecked(s, 3, 600).warning, Capabilities.ResolveBitrateChecked(s, 3, 1800).warning };
        using var scope = L.Scope("qps-ploc");
        bool[] pseudo = { Capabilities.ResolveBitrateChecked(s, 3, 45).warning, Capabilities.ResolveBitrateChecked(s, 3, 600).warning, Capabilities.ResolveBitrateChecked(s, 3, 1800).warning };
        Check.Equal(english, pseudo);
        Check.Equal(new[] { false, true, true }, english);
    }

    [UnitTest]
    public static void Import_notices_and_labels_come_from_the_catalog_and_read_as_before_in_English()
    {
        var copy = new ForeignSettings("Contoso.Notes_pzs8sxrjxfjjc", @"C:\x", new DateTime(2026, 10, 21, 18, 32, 5, DateTimeKind.Utc), new[] { "video quality", "replay length", "hotkeys" });
        Check.Equal("video quality, replay length, hotkeys", SettingsImport.LabelList(copy.Differences));
        Check.Equal("Settings imported; the clip list could not be merged: boom", SettingsImport.ClipsNoticeFor("boom"));
        Check.Equal("Settings imported; a network folder in the other copy is never taken over silently, so your own stayed: screenshots folder, archive location (set a network folder yourself under Settings if you want one)",
            SettingsImport.NetworkPathsNoticeFor(new[] { nameof(Settings.ScreenshotsFolder), nameof(Settings.ArchivePath) }));
        Check.Equal("Clink was started from inside Notes: Windows keeps a private copy of its settings for that app, so changes made now are not seen by Clink started from Explorer or the Start menu",
            AppDataRedirect.NoticeFor("Notes"));

        using var scope = L.Scope("qps-ploc");
        Marked(MainWindow.ImportBannerText(copy), "import banner");
        Marked(MainWindow.ImportBannerText(copy with { Differences = Array.Empty<string>() }), "import banner without changes");
        Marked(SettingsImport.ClipsNoticeFor("boom"), "clips notice");
        Marked(SettingsImport.NetworkPathsNoticeFor(new[] { nameof(Settings.ClipsFolder) }), "network folders notice");
        Marked(AppDataRedirect.NoticeFor("Notes"), "redirect notice");
        // every label has its own catalog entry and is not left as English pieces
        var labels = (typeof(SettingsImport).GetField("s_labels", Any)!.GetValue(null) as IEnumerable<(string Label, string[] Keys)>)!.Select(l => l.Label)
            .Concat(new[] { "other settings", "screenshots folder", "archive location", "clips folder" }).Distinct().ToList();
        Check.True(labels.Count >= 20, "read the labels: " + labels.Count);
        foreach (string label in labels) Marked(SettingsImport.LabelText(label), "label " + label);
        L.ClearMissing();
        foreach (string label in labels) SettingsImport.LabelText(label);
        SettingsImport.LabelList(labels);
        None(L.Missing.ToList(), "labels without a catalog entry");
    }

    [UnitTest]
    public static void A_clip_list_that_cannot_be_merged_gives_a_reason_in_the_user_language_and_an_English_log_line()
    {
        using var folder = new TempDir();
        File.WriteAllText(folder.File("clips.json"), "{ this is not json");
        L.ClearMissing();
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        Log.Line += Collect;
        string? en, pseudo;
        try
        {
            en = SettingsImport.TryMergeClips(folder.Path);
            using (L.Scope("qps-ploc")) pseudo = SettingsImport.TryMergeClips(folder.Path);
        }
        finally { Log.Line -= Collect; }
        Check.NotNull(en);
        Check.True(en!.StartsWith("the other copy's clip list cannot be read (", StringComparison.Ordinal), en);
        Marked(pseudo, "the reason under the pseudo-locale");
        string[] logged;
        lock (lines) logged = lines.Where(l => l.Contains("the clip list was not merged", StringComparison.Ordinal)).ToArray();
        Check.Equal(2, logged.Length);
        Check.False(logged.Any(l => PseudoLocale.IsPseudo(l)), "the log stays English: " + string.Join(" | ", logged));
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void The_pending_clip_merge_keeps_its_reason_in_English_whatever_the_user_language()
    {
        using var folder = new TempDir();
        File.WriteAllText(folder.File("clips.json"), "{ this is not json");
        L.ClearMissing();
        try
        {
            SettingsImport.ForgetPendingClipMerge();
            var merge = typeof(SettingsImport).GetMethod("MergeClipIndex", Any) ?? throw new TestFailure("no MergeClipIndex");
            string? shown;
            using (L.Scope("qps-ploc")) shown = merge.Invoke(null, new object[] { folder.Path }) as string;
            Marked(shown, "the notice the user reads");
            var pending = SettingsImport.PendingClipMerge();
            Check.NotNull(pending);
            Check.True(pending!.Value.Reason.StartsWith("the other copy's clip list cannot be read (", StringComparison.Ordinal), "stored in English: " + pending.Value.Reason);
            Check.False(PseudoLocale.IsPseudo(pending.Value.Reason), "no pseudo text in the marker file: " + pending.Value.Reason);
            Check.Equal(1, pending.Value.Attempts);

            // the retry at the next start (still failing) rewrites it, again in English
            SettingsImport.TakeClipsNotice();
            using (L.Scope("qps-ploc")) SettingsImport.RetryPendingClipMerge();
            pending = SettingsImport.PendingClipMerge();
            Check.Equal(2, pending!.Value.Attempts);
            Check.False(PseudoLocale.IsPseudo(pending.Value.Reason), "the retry keeps it English: " + pending.Value.Reason);
            Check.False(File.ReadAllText(SettingsImport.ClipsMarkerPath).Contains("[!!", StringComparison.Ordinal), "on disk");
        }
        finally { SettingsImport.ForgetPendingClipMerge(); SettingsImport.TakeClipsNotice(); }
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void The_network_folder_note_is_kept_as_names_and_worded_when_shown()
    {
        string path = SettingsImport.NetworkPathsNoticePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // the new format: the setting names (what Import writes), the sentence built at display time in the language of that moment
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { nameof(Settings.ScreenshotsFolder), nameof(Settings.ArchivePath) }));
            string? pseudo;
            using (L.Scope("qps-ploc")) pseudo = SettingsImport.TakeNetworkPathsNotice();
            Marked(pseudo, "the note shown under the pseudo-locale");
            Check.False(File.Exists(path), "the note is said once");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { nameof(Settings.ScreenshotsFolder), nameof(Settings.ArchivePath) }));
            Check.Equal(SettingsImport.NetworkPathsNoticeFor(new[] { nameof(Settings.ScreenshotsFolder), nameof(Settings.ArchivePath) }), SettingsImport.TakeNetworkPathsNotice());

            // an older file holds the sentence itself: shown as it is
            File.WriteAllText(path, "Settings imported; the old sentence");
            Check.Equal("Settings imported; the old sentence", SettingsImport.TakeNetworkPathsNotice());
            // a "[" that is no list of names is never shown as raw JSON
            foreach (string bad in new[] { "[not json", "[\"Scr", "[1]", "[\"a\",null]", "[\"UnknownName\"]", "   \r\n ", "[]", "[null]" })
            {
                File.WriteAllText(path, bad);
                Check.Null(SettingsImport.TakeNetworkPathsNotice(), "nothing shown for: " + bad);
                Check.False(File.Exists(path), "taken: " + bad);
            }
            // known names stay when an unknown one sits beside them
            File.WriteAllText(path, "[\"ScreenshotsFolder\",\"UnknownName\"]");
            Check.Equal(SettingsImport.NetworkPathsNoticeFor(new[] { nameof(Settings.ScreenshotsFolder) }), SettingsImport.TakeNetworkPathsNotice());
            File.WriteAllText(path, "[\"ScreenshotsFolder\",null]");
            Check.Equal(SettingsImport.NetworkPathsNoticeFor(new[] { nameof(Settings.ScreenshotsFolder) }), SettingsImport.TakeNetworkPathsNotice());
            Check.Null(SettingsImport.TakeNetworkPathsNotice());   // gone
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [UnitTest]
    public static void The_clip_merge_notice_is_worded_when_taken_not_when_made()
    {
        using var folder = new TempDir();
        File.WriteAllText(folder.File("clips.json"), "{ this is not json");
        L.ClearMissing();
        try
        {
            SettingsImport.ForgetPendingClipMerge();
            var merge = typeof(SettingsImport).GetMethod("MergeClipIndex", Any) ?? throw new TestFailure("no MergeClipIndex");
            using (L.Scope("en")) merge.Invoke(null, new object[] { folder.Path });   // made before the UI language is set
            string? pseudo;
            using (L.Scope("qps-ploc")) pseudo = SettingsImport.TakeClipsNotice();
            Marked(pseudo, "the notice taken under the pseudo-locale");
            Check.Null(SettingsImport.TakeClipsNotice(), "once");

            // the same made under en and taken under en: today's English
            using (L.Scope("en"))
            {
                SettingsImport.ForgetPendingClipMerge();
                merge.Invoke(null, new object[] { folder.Path });
                string? en = SettingsImport.TakeClipsNotice();
                Check.True(en != null && en.StartsWith("Settings imported; the clip list could not be merged: the other copy's clip list cannot be read (", StringComparison.Ordinal), "English: " + en);
                Check.False(PseudoLocale.IsPseudo(en!), "no pseudo text under en");
            }

            // the retry at start (before the language is set) is worded when taken, too
            SettingsImport.ForgetPendingClipMerge();
            using (L.Scope("en")) merge.Invoke(null, new object[] { folder.Path });
            SettingsImport.TakeClipsNotice();
            // Attempts is 1 now: the retry says it again
            string? retry;
            using (L.Scope("en")) SettingsImport.RetryPendingClipMerge();
            using (L.Scope("qps-ploc")) retry = SettingsImport.TakeClipsNotice();
            Marked(retry, "the retry's notice taken under the pseudo-locale");
        }
        finally { SettingsImport.ForgetPendingClipMerge(); SettingsImport.TakeClipsNotice(); }
        None(L.Missing.ToList(), "keys asked for and not in the English catalog");
    }

    [UnitTest]
    public static void The_toast_labels_have_no_raw_number_in_the_XAML_and_Populate_sets_them()
    {
        string? src = L10nSourceScan.Root();
        if (src == null) return;
        string xaml = File.ReadAllText(Path.Combine(src, "MainWindow.xaml"));
        foreach (string name in new[] { "ToastClipLabel", "ToastSharedLabel", "ToastScreenshotLabel", "ToastRecordingLabel" })
            Check.True(System.Text.RegularExpressions.Regex.IsMatch(xaml, "x:Name=\"" + name + "\"[^>]*Text=\"\""), name + " starts empty");
        string code = File.ReadAllText(Path.Combine(src, "MainWindow.xaml.cs"));
        int populate = code.IndexOf("ToastScreenshotSlider.Value = Math.Clamp", StringComparison.Ordinal);
        Check.True(populate > 0 && code.IndexOf("UpdateToastLabels();", populate, StringComparison.Ordinal) is int at && at > 0 && at - populate < 200, "Populate fills the labels right after the sliders");
    }

    // ------------------------------------------------------------------ the keys the slice's sources ask for exist in the English catalog

    [UnitTest]
    public static void Every_key_in_the_slice_sources_is_in_main_or_core_json()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // not run from a checkout: nothing to read
        var area = L10nSourceScan.AreaKeys(root, "main");
        var core = L10nSourceScan.AreaKeys(root, "core");
        var problems = new List<string>();

        // the helpers of these files that take a key (L.Plural( is not used by them)
        var hits = L10nSourceScan.CSharp(root,
            new[] { "MainWindow.xaml.cs", @"UI\HotkeyListEditor.xaml.cs", @"UI\ReplayLengthSlider.cs", @"Services\Capabilities.cs", @"Services\SettingsImport.cs", @"Services\CopiumCardModel.cs" },
            new[] { L10nSourceScan.T, L10nSourceScan.TInEn, L10nSourceScan.TrLocale, L10nSourceScan.Skipped, L10nSourceScan.SkippedBy });
        hits.AddRange(L10nSourceScan.Xaml(root, new[] { "MainWindow.xaml", @"UI\HotkeyListEditor.xaml" }));
        foreach (var h in hits)
            if (h.Key != "setting|" && !area.Contains(h.Key) && !core.Contains(h.Key)) problems.Add(h.File + ": " + h.Key);   // "setting|" is the prefix SettingsImport.LabelText puts before a label (the labels are checked by their own test)

        var english = L.Catalogs.English.Entries;
        Check.True(hits.Count > 250, "found the keys of the slice's sources: " + hits.Count);
        None(problems, "keys missing from main.json / core.json");

        // the sources that hold English as catalog keys (not as L.T( literals)
        foreach (string key in new[]
        {
            "size-target|None", "Discord free (20 MB)", "Copium Plus (2 GB, 60 min)", "not supported by this GPU", "theme|Dark", "theme|Light", "accent|Spark Blue", "accent|Green",
            "hotkey-action|Clip", "Pause / resume", "FPS", "verb|Update", "verb|Test", "verb|Preview", "tab|Capture", "replay-length|ideal", "resolution|Native",
            "position|Top left", "position|Top", "position|Top right", "position|Left", "position|Right", "position|Bottom left", "position|Bottom", "position|Bottom right",
            "track|Mix", "track|Game", "track|Mic", "track|Voice chat", "track|Desktop", "Share last clip", "Start / stop recording", "Take screenshot", "Edit last screenshot",
            "Save only", "Open editor", "Edit on screen",
            "plays everywhere, incl. Discord inline and every browser; biggest files.",
            "Copium free uploads are limited to 10 min: trim a full-length clip before sharing it, or shorten the replay length",
            "Copium Plus uploads are limited to 60 min: trim a full-length clip before sharing it, or shorten the replay length",
            "list-separator|, ", "unit|{n} m", "unit|{n} Mbps",
        })
            Check.True(english.ContainsKey(key), "the English catalog has \"" + key + "\"");
    }
}
