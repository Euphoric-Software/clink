using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// The Language picker (App tab, first card) and the restart that applies a language: where the card is, what it offers, that a pick is saved
/// and read back, when the "Restart Clink to use the new language." hint shows and that it is written in the language just chosen, and
/// the restart itself (no real process is started, no window is shown): the confirmation while a recording runs, --dev-language dropped,
/// and a restart that fails.
/// </summary>
internal static class LanguageSettingTests
{
    private const string Auto = "Automatic (Windows display language)";
    private const string Hint = "Restart Clink to use the new language.";

    // ------------------------------------------------------------------ helpers

    private static void NeedsApp()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
    }

    /// <summary>English, German and Arabic, each with the two texts the card needs (the Arabic one is a marked English text: only its direction matters here).</summary>
    private static CatalogSet ThreeLanguages() => CatalogSet.Build(new[]
    {
        new CatalogSource("en", "t", $$"""{ "{{Auto}}": "{{Auto}}", "{{Hint}}": "{{Hint}}" }"""),
        new CatalogSource("de", "t", $$"""{ "{{Auto}}": "Automatisch (Windows-Anzeigesprache)", "{{Hint}}": "Starte Clink neu, um die neue Sprache zu verwenden." }"""),
        new CatalogSource("ar", "t", $$"""{ "{{Auto}}": "[ar] {{Auto}}", "{{Hint}}": "[ar] {{Hint}}" }"""),
    });

    private static void SetLoaded(MainWindow w)
    {
        var field = typeof(MainWindow).GetField("_loading", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("MainWindow._loading is gone");
        field.SetValue(w, false);
    }

    private static ComboBoxItem Item(MainWindow w, string tag) =>
        w.LanguageCombo.Items.OfType<ComboBoxItem>().Single(i => (string?)i.Tag == tag);

    private static string[] Labels(MainWindow w) => w.LanguageCombo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Content).ToArray();

    private static string NativeName(string code) => L.Known.Single(k => k.Code == code).NativeName;

    /// <summary>A controller with settings in a scratch folder and a fake way to start and end processes and to answer the recording question.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Settings S;
        public readonly AppController App;
        public readonly List<ProcessStartInfo> Started = new();
        public readonly List<string> Asked = new();
        public readonly List<string> Notices = new();
        public int Exits;
        public bool FailStart;
        public MessageBoxResult Answer = MessageBoxResult.Yes;

        public Rig()
        {
            Settings.DirOverride = Dir.Path;
            ClipLibrary.IndexPathOverride = Dir.File("clips.json");
            S = Settings.FromJson("{}");
            S.ClickSound = false;
            S.CaptureBorderNoticeDismissed = true;
            S.ClipsFolder = Dir.File("clips");
            Directory.CreateDirectory(S.ClipsFolder);
            App = new AppController(S, new Updater(S))
            {
                CommandLine = new[] { "--minimized", "--no-update" },
                ExitForRestart = () => Exits++,
                StartProcess = psi =>
                {
                    Started.Add(psi);
                    if (FailStart) throw new InvalidOperationException("no memory for another Clink");
                },
                LanguageRestartAsk = (_, text) => { Asked.Add(text); return Answer; },
            };
            App.Notice += msg => Notices.Add(msg);
        }

        public void StartRecording() =>
            (typeof(AppController).GetField("_wantRecording", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController._wantRecording is gone")).SetValue(App, true);

        public void Dispose()
        {
            AppController.InstanceSuffix = "";
            AppController.DevLanguage = null;
            App.Dispose();
            Dir.Dispose();
        }
    }

    // ------------------------------------------------------------------ the card

    [UnitTest]
    public static void The_language_card_is_the_first_card_of_the_App_tab_with_the_exact_labels()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        var stack = (StackPanel)((ScrollViewer)w.AppTab.Content).Content;
        var headings = stack.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
        Check.Equal(new[] { "LANGUAGE", "FEATURES", "CLOUD SHARING", "COPIUM", "APPEARANCE", "UPDATES", "SETTINGS", "ABOUT" }, headings, "LANGUAGE comes first, the other headings are as they were");
        Check.True(stack.Children[0] is TextBlock { Text: "LANGUAGE" }, "the heading is the first element");
        var card = stack.Children[1] as Border ?? throw new TestFailure("the card follows its heading");
        Check.True(card.IsAncestorOf(w.LanguageCombo), "the picker is in the first card");
        Check.True(card.IsAncestorOf(w.LanguageRestartButton) && card.IsAncestorOf(w.LanguageRestartHint), "so are the restart hint and button");
        Check.Equal("Language", AutomationProperties.GetName(w.LanguageCombo), "the picker's accessible name");
        Check.Equal("Restart now", (string)w.LanguageRestartButton.Content);
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility, "no hint before anything is chosen");
        var texts = stack.Children.OfType<Border>().SelectMany(b => Descendants(b)).OfType<TextBlock>().Select(t => t.Text).ToArray();
        Check.True(texts.Contains("Language"), "the label next to the picker");
        // the English of the cards that were already there is untouched
        var features = (CheckBox)w.FeatureClipsCheck;
        Check.Equal("Clips", (string)features.Content);
        Check.Equal("Screenshots", (string)w.FeatureScreenshotsCheck.Content);
        Check.Equal("Allow cloud sharing (Copium uploads for clips and screenshots)", (string)w.CloudSharingCheck.Content);
        Check.Equal("Install updates automatically (checks every 6 hours)", (string)w.AutoUpdateCheck.Content);
        Check.Equal("Run at startup", (string)w.StartupCheck.Content);
        Check.Contains(string.Join("|", texts), "The accent colours buttons, toggles, sliders and the selected tab.");
        Check.Contains(string.Join("|", texts), "replay buffer, recording, clip library");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d)
            {
                yield return d;
                foreach (var inner in Descendants(d)) yield return inner;
            }
    }

    [UnitTest]
    public static void The_picker_offers_Automatic_then_the_available_languages_by_their_own_names_and_never_a_pseudo_locale()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        w.FillLanguageCard(rig.S);
        var expected = new[] { Auto }.Concat(L.Known.Where(k => L.Available.Contains(k.Code)).Select(k => k.NativeName)).ToArray();
        Check.Equal(expected, Labels(w), "Automatic, then every available language");
        Check.True(Labels(w).Length >= 2, "at least Automatic and English");
        Check.Equal(17, Labels(w).Length, "Automatic plus all 16 languages");
        Check.Equal("English", Labels(w)[1], "English is the first language");
        Check.Equal(new[] { "auto" }.Concat(L.Known.Where(k => L.Available.Contains(k.Code)).Select(k => k.Code)).ToArray(), w.LanguageCombo.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).ToArray(), "the values saved");
        Check.False(Labels(w).Any(l => l.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) || l.Contains("qps", StringComparison.OrdinalIgnoreCase)), "no pseudo-locale");
        Check.Equal("auto", (string)((ComboBoxItem)w.LanguageCombo.SelectedItem).Tag, "a new install starts on Automatic");

        // with three languages complete: Automatic, English, Deutsch, the Arabic name; each in its own direction
        using var catalogs = L.UseCatalogs(ThreeLanguages());
        w.FillLanguageCard(rig.S);
        Check.Equal(new[] { Auto, "English", "Deutsch", NativeName("ar") }, Labels(w));
        Check.Equal(FlowDirection.LeftToRight, Item(w, "en").FlowDirection);
        Check.Equal(FlowDirection.LeftToRight, Item(w, "de").FlowDirection);
        Check.Equal(FlowDirection.RightToLeft, Item(w, "ar").FlowDirection, "the Arabic name lays out right to left");
        Check.Equal(NativeName("ar"), (string)Item(w, "ar").Content, "and is not translated");
    }

    [UnitTest]
    public static void A_pick_is_saved_and_read_back_and_filling_the_card_is_not_a_pick()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        using var catalogs = L.UseCatalogs(ThreeLanguages());
        w.FillLanguageCard(rig.S);
        SetLoaded(w);
        w.WindowsLanguages = () => new[] { "en-US" };

        w.LanguageCombo.SelectedItem = Item(w, "de");
        Check.Equal("de", rig.S.Language);
        Check.Equal("de", Settings.Load().Language, "saved to settings.json");
        w.LanguageCombo.SelectedItem = Item(w, "auto");
        Check.Equal("auto", rig.S.Language);
        Check.Equal("auto", Settings.Load().Language);
        w.LanguageCombo.SelectedItem = Item(w, "ar");
        Check.Equal("ar", Settings.Load().Language);

        // a fresh window shows the saved choice, and writing nothing
        rig.S.Language = "de";
        w.FillLanguageCard(rig.S);
        Check.Equal("de", (string)((ComboBoxItem)w.LanguageCombo.SelectedItem).Tag);
        Check.Equal("de", rig.S.Language);
        // a choice no longer on the list (saved by a build with more languages) shows as Automatic and is left alone
        rig.S.Language = "ja";
        w.FillLanguageCard(rig.S);
        Check.Equal("auto", (string)((ComboBoxItem)w.LanguageCombo.SelectedItem).Tag);
        Check.Equal("ja", rig.S.Language, "shown, not rewritten");

        // the import takes the choice along
        Check.True(SettingsImport.HasLabelOrIsVolatile(nameof(Settings.Language)), "the import offer names the Language setting");
        var ours = Settings.FromJson("{}");
        var theirs = Settings.FromJson("{}");
        theirs.Language = "de";
        Check.Equal(new[] { "language" }, SettingsImport.Differences(ours.ToJson(), theirs.ToJson()).ToArray(), "another copy's language is an import difference, under its own label (not volatile)");
        Check.Equal("language", SettingsImport.LabelText("language"), "and the label reads as it does in English");
    }

    // ------------------------------------------------------------------ the hint

    [UnitTest]
    public static void The_restart_hint_shows_only_when_the_choice_is_not_the_running_language_and_is_written_in_the_new_language()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        using var catalogs = L.UseCatalogs(ThreeLanguages());
        using var running = L.Scope("en");
        w.FillLanguageCard(rig.S);
        SetLoaded(w);
        w.WindowsLanguages = () => new[] { "en-US" };
        w.UpdateLanguageHint();
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility, "Automatic on an English Windows is the running language");
        Check.Equal("", w.LanguageRestartHint.Text);

        w.LanguageCombo.SelectedItem = Item(w, "de");
        Check.Equal(Visibility.Visible, w.LanguageRestartPanel.Visibility, "German is not what runs");
        Check.Equal("Starte Clink neu, um die neue Sprache zu verwenden.", w.LanguageRestartHint.Text, "the hint is in German, the language just chosen, though Clink runs in English");
        Check.Equal(L.TIn("de", Hint), w.LanguageRestartHint.Text, "through L.TIn");
        Check.NotEqual(L.T(Hint), w.LanguageRestartHint.Text, "not the running language's text");
        Check.Equal(FlowDirection.LeftToRight, w.LanguageRestartHint.FlowDirection);
        Check.Equal("Restart now", (string)w.LanguageRestartButton.Content, "the button stays in the running language");

        w.LanguageCombo.SelectedItem = Item(w, "ar");
        Check.Equal(Visibility.Visible, w.LanguageRestartPanel.Visibility);
        Check.Equal("[ar] " + Hint, w.LanguageRestartHint.Text);
        Check.Equal(FlowDirection.RightToLeft, w.LanguageRestartHint.FlowDirection, "an Arabic hint lays out right to left");

        w.LanguageCombo.SelectedItem = Item(w, "en");
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility, "back to English: the choice is the running language again");
        Check.Equal("", w.LanguageRestartHint.Text);

        // Automatic follows the Windows list: German Windows is not English...
        w.WindowsLanguages = () => new[] { "de-DE", "en-US" };
        w.LanguageCombo.SelectedItem = Item(w, "auto");
        Check.Equal(Visibility.Visible, w.LanguageRestartPanel.Visibility);
        Check.Equal("Starte Clink neu, um die neue Sprache zu verwenden.", w.LanguageRestartHint.Text);
        // ...a Windows language Clink does not have falls through to English...
        w.WindowsLanguages = () => new[] { "ja-JP" };
        w.UpdateLanguageHint();
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility);
        // ...and Traditional Chinese is never answered with Simplified
        w.WindowsLanguages = () => new[] { "zh-TW", "en-US" };
        w.UpdateLanguageHint();
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility);
    }

    [UnitTest]
    public static void When_Clink_runs_in_another_language_picking_English_asks_for_a_restart_in_English()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        using var catalogs = L.UseCatalogs(ThreeLanguages());
        using var running = L.Scope("de");
        w.FillLanguageCard(rig.S);
        SetLoaded(w);
        w.WindowsLanguages = () => new[] { "de-DE" };
        w.UpdateLanguageHint();
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility, "Automatic on a German Windows is German");
        w.LanguageCombo.SelectedItem = Item(w, "en");
        Check.Equal(Visibility.Visible, w.LanguageRestartPanel.Visibility);
        Check.Equal(Hint, w.LanguageRestartHint.Text, "in English, the language just chosen");
        w.LanguageCombo.SelectedItem = Item(w, "de");
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility);
    }

    [UnitTest]
    public static void A_pseudo_locale_counts_as_English_for_the_hint()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        using var running = L.Scope(L.PseudoCode);
        w.FillLanguageCard(rig.S);
        SetLoaded(w);
        w.WindowsLanguages = () => new[] { "en-US" };
        w.UpdateLanguageHint();
        Check.Equal(Visibility.Collapsed, w.LanguageRestartPanel.Visibility, "a look at the layout is not a language to restart out of");
        Check.Equal("", w.LanguageRestartHint.Text);
    }

    // ------------------------------------------------------------------ the restart

    [UnitTest]
    public static void Restart_now_saves_the_choice_and_restarts_through_the_one_restart_path()
    {
        NeedsApp();
        using var rig = new Rig();
        rig.S.Language = "de";
        Check.True(rig.App.RestartForLanguage(null), "the restart is under way");
        Check.Equal(1, rig.Started.Count, "the new Clink is started once");
        Check.Equal(new[] { "--show", "--after-pid", Environment.ProcessId.ToString() }, rig.Started[0].ArgumentList.ToArray(), "like an import's restart: --show, and waiting for this process");
        Check.Equal(1, rig.Exits, "and this process ends");
        Check.True(Settings.SavesFrozen, "nothing saves over the settings after that");
        Check.Equal(0, rig.Asked.Count, "no recording: nothing to ask");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(rig.Dir.File("settings.json")));
        Check.Equal("de", doc.RootElement.GetProperty("Language").GetString(), "the choice is in the file the new Clink reads");
        Check.False(rig.App.RestartForLanguage(null), "a second restart while one is under way is refused");
        Check.Equal(1, rig.Started.Count);
    }

    [UnitTest]
    public static void The_Restart_now_button_asks_the_controller_to_restart()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        w.LanguageRestartButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check.Equal(1, rig.Started.Count, "the click restarted");
        Check.Equal(1, rig.Exits);
    }

    [UnitTest]
    public static void While_a_recording_runs_the_restart_asks_first_in_the_current_language_and_says_the_replay_buffer_is_cleared()
    {
        NeedsApp();
        using var rig = new Rig();
        rig.StartRecording();
        Check.True(rig.App.IsManualRecording, "the test's recording is on");

        rig.Answer = MessageBoxResult.No;
        Check.False(rig.App.RestartForLanguage(null), "no keeps Clink as it is");
        Check.Equal(1, rig.Asked.Count);
        Check.Equal("A recording is running. Restarting Clink ends it and saves what was recorded, and the replay buffer is cleared.\n\nRestart Clink to change the language?", rig.Asked[0]);
        Check.Equal(0, rig.Started.Count);
        Check.Equal(0, rig.Exits);
        Check.False(Settings.SavesFrozen, "nothing was frozen");
        Check.True(rig.App.IsManualRecording, "and the recording goes on");

        rig.Answer = MessageBoxResult.Cancel;
        Check.False(rig.App.RestartForLanguage(null), "closing the box is no too");
        Check.Equal(0, rig.Started.Count);

        rig.Answer = MessageBoxResult.Yes;
        Check.True(rig.App.RestartForLanguage(null), "yes restarts");
        Check.Equal(3, rig.Asked.Count);
        Check.Equal(1, rig.Started.Count);
        Check.Equal(1, rig.Exits);
    }

    [UnitTest]
    public static void The_recording_question_is_asked_in_the_current_ui_language()
    {
        NeedsApp();
        using var rig = new Rig();
        rig.StartRecording();
        rig.Answer = MessageBoxResult.No;
        using (L.Scope("qps-ploc")) rig.App.RestartForLanguage(null);
        Check.True(PseudoLocale.IsPseudo(rig.Asked.Single()), "the question comes from the catalog of the language Clink runs in: " + rig.Asked.Single());
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in the English catalog");
    }

    [UnitTest]
    public static void A_language_restart_drops_dev_language_and_a_failed_one_puts_it_back()
    {
        NeedsApp();
        using var rig = new Rig();
        AppController.InstanceSuffix = ".dev.test";
        AppController.DevLanguage = "qps-plocm";
        var line = new[] { "--dev", rig.Dir.Path, "--dev-language", "qps-plocm", "--minimized" };
        rig.App.CommandLine = line;
        Check.True(AppController.RestartArguments(line).Contains("--dev-language"), "an ordinary restart (an import's) keeps the language of a dev instance");

        rig.FailStart = true;
        Check.False(rig.App.RestartForLanguage(null), "the new Clink could not be started");
        Check.Equal("qps-plocm", AppController.DevLanguage, "this Clink goes on as it was, so --dev-language is back");
        Check.Equal(1, rig.Started.Count);
        Check.Equal(0, rig.Exits);

        rig.FailStart = false;
        Check.True(rig.App.RestartForLanguage(null));
        var args = rig.Started[1].ArgumentList.ToArray();
        Check.True(args.Contains("--dev"), "still the same dev instance: " + string.Join(" ", args));
        Check.False(args.Contains("--dev-language"), "but without --dev-language, or the new language would be hidden: " + string.Join(" ", args));
        Check.False(args.Contains("qps-plocm"), string.Join(" ", args));
        Check.Equal(null, AppController.DevLanguage, "and not in force any more");
    }

    [UnitTest]
    public static void A_restart_that_cannot_start_Clink_goes_on_thaws_the_saves_and_tells_the_user()
    {
        NeedsApp();
        using var rig = new Rig();
        rig.S.Language = "de";
        rig.FailStart = true;
        Check.False(rig.App.RestartForLanguage(null));
        Check.Equal(0, rig.Exits, "this process stays");
        Check.False(Settings.SavesFrozen, "saves are thawed");
        Check.True(rig.Notices.Any(n => n.Contains("could not restart itself") && n.Contains("no memory for another Clink") && n.Contains("new language applies the next time Clink starts")), "the user is told: " + string.Join(" | ", rig.Notices));
        Check.Equal("de", Settings.Load().Language, "the choice stays saved: it applies at the next start");
        rig.FailStart = false;
        Check.True(rig.App.RestartForLanguage(null), "and a second try can restart");
    }

    // ------------------------------------------------------------------ the restart keeps the clip index live

    [UnitTest]
    public static void The_language_restart_leaves_the_clip_index_live_so_a_recording_that_ends_during_it_is_kept()
    {
        NeedsApp();
        using var rig = new Rig();
        Check.True(rig.App.RestartForLanguage(null), "the restart is under way");
        Check.True(Settings.SavesFrozen, "the settings are frozen (they were saved just before)");
        Check.False(rig.App.Library.SavesFrozen, "the clip index is not: a recording, a clip or a link that finishes during the restart is written");

        // the recording that ends after the restart call (the engine's end reaches the controller on the UI thread: AddRecording)
        string folder = Path.Combine(rig.S.ClipsFolder, "Recordings");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "after-restart.mp4");
        File.WriteAllBytes(file, new byte[64]);
        (typeof(AppController).GetMethod("AddRecording", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController.AddRecording is gone"))
            .Invoke(rig.App, new object?[] { file, 12.0, 64L, null, Array.Empty<string>() });
        Check.True(rig.App.Library.Find(file) != null, "it is in the library");
        Check.Contains(File.ReadAllText(rig.Dir.File("clips.json")), "after-restart.mp4", "and in clips.json, which the next Clink reads");
    }

    [UnitTest]
    public static void An_import_restart_still_freezes_the_clip_index()
    {
        NeedsApp();
        using var rig = new Rig();
        var stop = typeof(AppController).GetMethod("StopForRestart", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController.StopForRestart is gone");
        Check.Equal(1, stop.GetParameters().Length, "StopForRestart(freezeLibrary)");
        Check.True((bool)stop.GetParameters()[0].DefaultValue!, "frozen unless the caller says otherwise: an import's merged clips.json is not written over");
    }

    // ------------------------------------------------------------------ refused restarts, the modal question, the editor question

    [UnitTest]
    public static void A_restart_that_is_refused_tells_the_user()
    {
        NeedsApp();
        using var rig = new Rig();
        Check.True(rig.App.RestartForLanguage(null));
        rig.Notices.Clear();
        Check.False(rig.App.RestartForLanguage(null), "a second restart is refused");
        Check.True(rig.Notices.Contains("Clink is restarting"), "and the user is told: " + string.Join(" | ", rig.Notices));
        Check.Equal(1, rig.Started.Count);
    }

    [UnitTest]
    public static void A_restart_or_import_that_begins_while_the_recording_question_is_up_refuses_this_restart()
    {
        NeedsApp();
        foreach (string field in new[] { "_importInProgress", "_restarting", "_exiting" })
        {
            using var rig = new Rig();
            rig.StartRecording();
            rig.App.LanguageRestartAsk = (_, text) =>
            {
                rig.Asked.Add(text);
                (typeof(AppController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController." + field + " is gone")).SetValue(rig.App, true);   // a tray import restarted inside the modal loop
                return MessageBoxResult.Yes;
            };
            Check.False(rig.App.RestartForLanguage(null), field + ": the answer is yes, but the app is on its way out already");
            Check.Equal(1, rig.Asked.Count, field);
            Check.Equal(0, rig.Started.Count, field + ": no second process is started");
            Check.Equal(0, rig.Exits, field);
            Check.True(rig.Notices.Contains("Clink is restarting"), field + ": the user is told: " + string.Join(" | ", rig.Notices));
        }
    }

    [UnitTest]
    public static void An_open_screenshot_editor_asks_its_quit_question_first_and_cancel_keeps_Clink_running()
    {
        NeedsApp();
        using var rig = new Rig();
        var asked = new List<bool>();
        rig.App.LanguageEditorAsk = userInitiated => { asked.Add(userInitiated); return Task.FromResult(false); };
        Check.False(rig.App.RestartForLanguage(null), "cancel at the editor's question: no restart");
        Check.Equal(new[] { true }, asked.ToArray(), "asked as the user's own quit (it can be cancelled)");
        Check.Equal(0, rig.Started.Count);
        Check.Equal(0, rig.Exits);
        Check.False(Settings.SavesFrozen, "nothing was frozen");
        Check.False(rig.App.ImportInProgress, "and the restart is free again");

        rig.App.LanguageEditorAsk = userInitiated => { asked.Add(userInitiated); return Task.FromResult(true); };
        Check.True(rig.App.RestartForLanguage(null), "saved or discarded: the restart goes on");
        Check.Equal(1, rig.Started.Count);
        Check.Equal(1, rig.Exits);
    }

    [UnitTest]
    public static void The_editor_question_is_awaited_while_it_is_up_and_a_second_restart_is_refused_meanwhile()
    {
        NeedsApp();
        using var rig = new Rig();
        var answer = new TaskCompletionSource<bool>();
        rig.App.LanguageEditorAsk = _ => answer.Task;
        Check.True(rig.App.RestartForLanguage(null), "the question is up: the restart is under way");
        Check.Equal(0, rig.Started.Count, "nothing starts before the answer");
        Check.False(rig.App.RestartForLanguage(null), "a second request while the question is up is refused");
        answer.SetResult(true);
        ScreenshotUploadTests.Pump(() => rig.Started.Count == 1, "the restart after the answer", 5_000);
        Check.Equal(1, rig.Exits);

        using var rig2 = new Rig();
        var no = new TaskCompletionSource<bool>();
        rig2.App.LanguageEditorAsk = _ => no.Task;
        Check.True(rig2.App.RestartForLanguage(null));
        no.SetResult(false);
        ScreenshotUploadTests.Pump(() => !rig2.App.ImportInProgress, "cancel frees the restart", 5_000);
        Check.Equal(0, rig2.Started.Count);
    }

    private static object? Priv(AppController app, string field) =>
        (typeof(AppController).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController." + field + " is gone")).GetValue(app);

    private static void CallQuitAsync(AppController app, bool userInitiated) =>
        (typeof(AppController).GetMethod("QuitAsync", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController.QuitAsync is gone")).Invoke(app, new object[] { userInitiated });

    [UnitTest]
    public static void While_the_editor_question_is_up_a_tray_quit_cannot_open_a_second_save_box()
    {
        NeedsApp();
        using var rig = new Rig();
        var answer = new TaskCompletionSource<bool>();
        rig.App.LanguageEditorAsk = _ => answer.Task;
        Check.True(rig.App.RestartForLanguage(null));
        Check.True((bool)Priv(rig.App, "_quitAsking")!, "the language path holds the quit question");
        CallQuitAsync(rig.App, true);   // the tray's Quit: with the question held it returns at once (no second box, nothing shut down)
        Check.False((bool)Priv(rig.App, "_forcedQuitPending")!, "a user's quit is not remembered as forced");
        Check.Equal(0, rig.Started.Count);
        answer.SetResult(false);
        ScreenshotUploadTests.Pump(() => !rig.App.ImportInProgress, "cancel frees the restart", 5_000);
        Check.False((bool)Priv(rig.App, "_quitAsking")!, "and the quit question is free again");
        Check.Equal(0, rig.Started.Count);
    }

    [UnitTest]
    public static void A_forced_quit_during_the_editor_question_wins_over_the_language_restart()
    {
        NeedsApp();
        foreach (bool answerGo in new[] { true, false })
        {
            using var rig = new Rig();
            var quits = new List<bool>();
            rig.App.QuitOverride = userInitiated => quits.Add(userInitiated);
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = _ => answer.Task;
            Check.True(rig.App.RestartForLanguage(null));
            CallQuitAsync(rig.App, false);   // an update restart asks to quit: it cannot be refused
            Check.True((bool)Priv(rig.App, "_forcedQuitPending")!, "remembered");
            answer.SetResult(answerGo);
            ScreenshotUploadTests.Pump(() => quits.Count == 1, $"the quit goes on (answer {answerGo})", 5_000);
            Check.Equal(new[] { false }, quits.ToArray(), "as a forced quit");
            Check.Equal(0, rig.Started.Count, "no language restart");
            Check.Equal(0, rig.Exits);
            Check.False((bool)Priv(rig.App, "_quitAsking")!, "the question is released");
            Check.False((bool)Priv(rig.App, "_forcedQuitPending")!);
        }
    }

    private static bool ResolveEditorForExit(AppController app, bool userInitiated) =>
        ((Task<bool>)(typeof(AppController).GetMethod("ResolveEditorForQuitAsync", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController.ResolveEditorForQuitAsync is gone")).Invoke(app, new object[] { userInitiated })!).GetAwaiter().GetResult();

    [UnitTest]
    public static void An_answered_editor_question_is_not_asked_again_on_the_way_out_but_a_cancel_then_forced_quit_saves_a_copy()
    {
        NeedsApp();
        // yes (saved) or no (discarded) for the restart: the exit does not call the editor again (a no must not be saved by the exit)
        foreach (bool forcedToo in new[] { false, true })
        {
            using var rig = new Rig();
            var asked = new List<bool>();
            var quits = new List<bool>();
            rig.App.QuitOverride = u => quits.Add(u);
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = u => { asked.Add(u); return answer.Task; };
            Check.True(rig.App.RestartForLanguage(null));
            if (forcedToo) CallQuitAsync(rig.App, false);
            answer.SetResult(true);
            ScreenshotUploadTests.Pump(() => forcedToo ? quits.Count == 1 : rig.Started.Count == 1, $"the exit goes on (forced quit too: {forcedToo})", 5_000);
            Check.True((bool)Priv(rig.App, "_editorResolvedForExit")!, "the editor is marked as answered");
            Check.True(ResolveEditorForExit(rig.App, false), "the exit path goes on");
            Check.Equal(new[] { true }, asked.ToArray(), "and the editor was not asked a second time");
        }

        // cancel, then a forced quit that came in meanwhile: the editor is asked again on the way out (it saves a copy)
        using (var rig = new Rig())
        {
            var asked = new List<bool>();
            var quits = new List<bool>();
            rig.App.QuitOverride = u => quits.Add(u);
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = u => { asked.Add(u); return asked.Count == 1 ? answer.Task : Task.FromResult(true); };
            Check.True(rig.App.RestartForLanguage(null));
            CallQuitAsync(rig.App, false);
            answer.SetResult(false);
            ScreenshotUploadTests.Pump(() => quits.Count == 1, "the forced quit goes on", 5_000);
            Check.False((bool)Priv(rig.App, "_editorResolvedForExit")!, "a cancel resolves nothing");
            Check.True(ResolveEditorForExit(rig.App, false), "the exit path saves the copy and goes on");
            Check.Equal(new[] { true, false }, asked.ToArray(), "asked again as a forced quit");
        }

        // a restart that cannot start goes on running: a later quit asks again
        using (var rig = new Rig())
        {
            rig.FailStart = true;
            rig.App.LanguageEditorAsk = _ => Task.FromResult(true);
            Check.False(rig.App.RestartForLanguage(null), "the new Clink could not be started");
            Check.False((bool)Priv(rig.App, "_editorResolvedForExit")!, "the mark is cleared");
        }
    }

    [UnitTest]
    public static void A_failing_recording_question_after_the_editor_answer_frees_the_quit_and_the_restart()
    {
        NeedsApp();
        using var rig = new Rig();
        var answer = new TaskCompletionSource<bool>();
        rig.App.LanguageEditorAsk = _ => answer.Task;
        rig.App.LanguageRestartAsk = (_, _) => throw new InvalidOperationException("the box failed");
        Check.True(rig.App.RestartForLanguage(null));
        rig.StartRecording();   // asked about after the editor's answer
        answer.SetResult(true);
        ScreenshotUploadTests.Pump(() => !rig.App.ImportInProgress, "the restart is free again", 5_000);
        Check.False((bool)Priv(rig.App, "_quitAsking")!, "a later quit is not swallowed");
        Check.False((bool)Priv(rig.App, "_forcedQuitPending")!);
        Check.Equal(0, rig.Started.Count, "not going ahead");
    }

    [UnitTest]
    public static void A_tray_quit_during_the_editor_question_quits_after_the_answer_instead_of_restarting()
    {
        NeedsApp();
        foreach (bool answerGo in new[] { true, false })
        {
            using var rig = new Rig();
            var quits = new List<bool>();
            rig.App.QuitOverride = u => quits.Add(u);
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = _ => answer.Task;
            Check.True(rig.App.RestartForLanguage(null));
            CallQuitAsync(rig.App, true);
            Check.True((bool)Priv(rig.App, "_userQuitPending")!, "remembered");
            Check.False((bool)Priv(rig.App, "_forcedQuitPending")!);
            answer.SetResult(answerGo);
            ScreenshotUploadTests.Pump(() => !(bool)Priv(rig.App, "_quitAsking")!, $"the question is released (answer {answerGo})", 5_000);
            Check.Equal(0, rig.Started.Count, "no language restart");
            Check.False((bool)Priv(rig.App, "_userQuitPending")!);
            if (answerGo)
            {
                Check.Equal(new[] { true }, quits.ToArray(), "the user's quit goes on after the editor's answer");
                Check.True((bool)Priv(rig.App, "_editorResolvedForExit")!, "and does not ask the editor again");
            }
            else
            {
                Check.Equal(0, quits.Count, "a cancelled editor question keeps Clink running");
                Check.False(rig.App.ImportInProgress);
            }
        }
    }

    [UnitTest]
    public static void A_cancelled_quit_question_forgets_a_second_tray_quit_so_a_later_language_restart_restarts()
    {
        NeedsApp();
        using var rig = new Rig();
        var quits = new List<bool>();
        rig.App.QuitOverride = u => quits.Add(u);
        var answer = new TaskCompletionSource<bool>();
        rig.App.LanguageEditorAsk = _ => answer.Task;
        CallQuitAsync(rig.App, true);   // the tray's Quit: its own editor question is up
        Check.True((bool)Priv(rig.App, "_quitAsking")!, "the quit holds the question");
        CallQuitAsync(rig.App, true);   // Quit again: remembered while the question is up
        Check.True((bool)Priv(rig.App, "_userQuitPending")!, "remembered");
        answer.SetResult(false);   // Cancel
        ScreenshotUploadTests.Pump(() => !(bool)Priv(rig.App, "_quitAsking")!, "the question is released", 5_000);
        Check.False((bool)Priv(rig.App, "_userQuitPending")!, "the cancelled quit leaves nothing remembered");
        rig.App.LanguageEditorAsk = _ => Task.FromResult(true);
        Check.True(rig.App.RestartForLanguage(null));
        ScreenshotUploadTests.Pump(() => rig.Started.Count == 1, "the language restart goes on", 5_000);
        Check.Equal(0, quits.Count, "and does not quit instead");
    }

    [UnitTest]
    public static void A_recording_that_starts_during_the_editor_question_is_asked_about_after_it()
    {
        NeedsApp();
        // the order kept: the recording question first when a recording is running at the start (a no there costs nothing), the editor's next;
        // a recording that began during the editor's question is asked about after it (it was not asked before)
        using (var rig = new Rig())
        {
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = _ => answer.Task;
            Check.True(rig.App.RestartForLanguage(null));
            Check.Equal(0, rig.Asked.Count, "no recording yet: no recording question");
            rig.StartRecording();
            answer.SetResult(true);
            ScreenshotUploadTests.Pump(() => rig.Started.Count == 1, "the restart after the recording question", 5_000);
            Check.Equal(1, rig.Asked.Count, "asked once, after the editor's answer");
            Check.Contains(rig.Asked[0], "A recording is running", "the recording question");
        }
        using (var rig = new Rig())
        {
            rig.Answer = MessageBoxResult.No;
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = _ => answer.Task;
            Check.True(rig.App.RestartForLanguage(null));
            rig.StartRecording();
            answer.SetResult(true);
            ScreenshotUploadTests.Pump(() => !rig.App.ImportInProgress, "a no to the recording question keeps Clink running", 5_000);
            Check.Equal(1, rig.Asked.Count);
            Check.Equal(0, rig.Started.Count);
            Check.False((bool)Priv(rig.App, "_quitAsking")!);
        }
        using (var rig = new Rig())   // already asked before the editor: not asked twice
        {
            rig.StartRecording();
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = _ => answer.Task;
            Check.True(rig.App.RestartForLanguage(null));
            Check.Equal(1, rig.Asked.Count, "asked first");
            answer.SetResult(true);
            ScreenshotUploadTests.Pump(() => rig.Started.Count == 1, "the restart", 5_000);
            Check.Equal(1, rig.Asked.Count, "and not again after the editor");
        }
        using (var rig = new Rig())   // the editor's question was cancelled: the recording question is not asked
        {
            var answer = new TaskCompletionSource<bool>();
            rig.App.LanguageEditorAsk = _ => answer.Task;
            Check.True(rig.App.RestartForLanguage(null));
            rig.StartRecording();
            answer.SetResult(false);
            ScreenshotUploadTests.Pump(() => !rig.App.ImportInProgress, "cancel", 5_000);
            Check.Equal(0, rig.Asked.Count);
            Check.Equal(0, rig.Started.Count);
        }
    }
    // ------------------------------------------------------------------ the panel

    [UnitTest]
    public static void The_restart_panel_shows_the_replay_buffer_sentence_and_the_hint_is_announced_in_its_own_language()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        using var catalogs = L.UseCatalogs(ThreeLanguages());
        using var running = L.Scope("en");
        w.FillLanguageCard(rig.S);
        SetLoaded(w);
        w.WindowsLanguages = () => new[] { "en-US" };
        Check.Equal("Restarting Clink clears the replay buffer: the moments recorded just before the restart are not kept.", w.LanguageRestartNote.Text, "visible text, not only a tooltip");
        Check.True(w.LanguageRestartPanel.IsAncestorOf(w.LanguageRestartNote), "in the restart panel");
        Check.Equal(w.LanguageRestartNote.Text, (string)w.LanguageRestartButton.ToolTip, "and still the button's tooltip");
        w.LanguageCombo.SelectedItem = Item(w, "de");
        Check.Equal("de", w.LanguageRestartHint.Language.IetfLanguageTag, "the hint is shaped for the language it is written in");
        Check.Equal("de", Item(w, "de").Language.IetfLanguageTag, "so is each name in the picker");
        Check.Equal("ar", Item(w, "ar").Language.IetfLanguageTag);
        Check.Equal(System.Windows.Automation.AutomationLiveSetting.Polite, System.Windows.Automation.AutomationProperties.GetLiveSetting(w.LanguageRestartHint), "a live region (the hint is set through SetLive)");
    }

    // ------------------------------------------------------------------ the archive rule

    private static string[] RulePieces(MainWindow w)
    {
        var pieces = new List<string>();
        void Add(UIElement e)
        {
            switch (e)
            {
                case TextBlock t: pieces.Add("T:" + t.Text); break;
                case TextBox b: pieces.Add(b == w.ArchiveDaysBox ? "[days]" : b == w.ArchiveGbBox ? "[size]" : "[?]"); break;
                case StackPanel p: foreach (UIElement c in p.Children) Add(c); break;
            }
        }
        foreach (UIElement c in w.ArchiveRulesPanel.Children) Add(c);
        return pieces.ToArray();
    }

    private static readonly string[] EnglishRule = { "T:Move clips older than", "[days]", "T:days, or when the local folder exceeds", "[size]", "T:GB \u00B7 whichever comes first" };

    [UnitTest]
    public static void The_archive_rule_is_laid_out_as_before_in_English()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        Check.Equal(EnglishRule, RulePieces(w), "the same pieces in the same order");
        var panel = w.ArchiveRulesPanel;
        Check.Equal(3, panel.Children.Count, "text, then two units (a box with the text after it), as before");
        Check.Equal(new Thickness(0, 0, 6, 4), ((TextBlock)panel.Children[0]).Margin);
        var units = new[] { (StackPanel)panel.Children[1], (StackPanel)panel.Children[2] };
        Check.True(units.All(u => u.Orientation == Orientation.Horizontal), "each unit stays on one line");
        Check.True(ReferenceEquals(units[0].Children[0], w.ArchiveDaysBox) && ReferenceEquals(units[1].Children[0], w.ArchiveGbBox), "the boxes keep their names");
        Check.Equal(new Thickness(0, 0, 6, 4), ((TextBlock)units[0].Children[1]).Margin);
        Check.Equal(new Thickness(0, 0, 0, 4), ((TextBlock)units[1].Children[1]).Margin);
        Check.Equal(46.0, w.ArchiveDaysBox.Width);
        Check.Equal(54.0, w.ArchiveGbBox.Width);
        Check.Equal("0 = off", (string)w.ArchiveDaysBox.ToolTip);
        Check.Equal("0 = off", (string)w.ArchiveGbBox.ToolTip);
        Check.Equal("Days before a clip moves to the archive", AutomationProperties.GetName(w.ArchiveDaysBox), "the days box has its own accessible name");
        Check.Equal("Local folder limit in GB", AutomationProperties.GetName(w.ArchiveGbBox), "so has the size box");
        Check.Equal(MainWindow.ArchiveRuleEnglish, "Move clips older than {days} days, or when the local folder exceeds {size} GB \u00B7 whichever comes first", "the one key");
        Check.True(L.Embedded.English.Entries.ContainsKey(MainWindow.ArchiveRuleEnglish), "it is in the English catalog");
        foreach (string gone in new[] { "Move clips older than", "days, or when the local folder exceeds", "GB \u00B7 whichever comes first" })
            Check.False(L.Embedded.English.Entries.ContainsKey(gone), "the fragment \"" + gone + "\" is gone");
    }

    [UnitTest]
    public static void A_translation_can_put_the_two_boxes_in_the_order_its_language_needs()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        w.BuildArchiveRulesRow("When the local folder exceeds {size} GB, or clips are older than {days} days, move them");
        Check.Equal(new[] { "T:When the local folder exceeds", "[size]", "T:GB, or clips are older than", "[days]", "T:days, move them" }, RulePieces(w), "size first, then days");
        Check.Equal(3, w.ArchiveRulesPanel.Children.Count);
        w.BuildArchiveRulesRow("{days} days or {size} GB");
        Check.Equal(new[] { "[days]", "T:days or", "[size]", "T:GB" }, RulePieces(w), "no text before the first box: none is made");
        w.BuildArchiveRulesRow("{size}{days}");
        Check.Equal(new[] { "[size]", "[days]" }, RulePieces(w), "boxes only");
        Check.Equal(2, w.ArchiveRulesPanel.Children.Count, "two units");
        Check.True(w.ArchiveRulesPanel.IsAncestorOf(w.ArchiveDaysBox) && w.ArchiveRulesPanel.IsAncestorOf(w.ArchiveGbBox), "both boxes are in the row, once");
        w.BuildArchiveRulesRow(MainWindow.ArchiveRuleEnglish);
        Check.Equal(EnglishRule, RulePieces(w), "and back to English");
    }

    [UnitTest]
    public static void An_archive_rule_without_both_places_falls_back_to_the_English_sentence()
    {
        NeedsApp();
        using var rig = new Rig();
        var w = new MainWindow();
        foreach (string broken in new[] { "Move clips older than {days} days", "{size} GB only", "no places at all", "", "{days} {days} {size}", "{days} {size} {size}" })
        {
            w.BuildArchiveRulesRow(broken);
            Check.Equal(EnglishRule, RulePieces(w), "\"" + broken + "\"");
        }
        Check.Equal(null, MainWindow.SplitArchiveRule("{days}"), "the splitter says so");
        var ok = MainWindow.SplitArchiveRule("a {size} b {days} c") ?? throw new TestFailure("a sentence with both places splits");
        Check.Equal(("a", "b", "c", false), (ok.Before, ok.Between, ok.After, ok.DaysFirst));
    }

    [UnitTest]
    public static void Under_the_pseudo_language_the_archive_rule_pieces_add_up_to_the_pseudo_sentence()
    {
        NeedsApp();
        using var rig = new Rig();
        using var pseudo = L.Scope(L.PseudoCode);
        var w = new MainWindow();
        string sentence = L.T(MainWindow.ArchiveRuleEnglish, ("days", "{days}"), ("size", "{size}"));
        Check.True(sentence.StartsWith("[!! ", StringComparison.Ordinal) && sentence.EndsWith(" !!]", StringComparison.Ordinal), "the pseudo sentence is framed: " + sentence);
        var pieces = RulePieces(w);
        Check.Equal(5, pieces.Length, string.Join(" | ", pieces));
        string joined = string.Join(" ", pieces.Select(p => p switch { "[days]" => "{days}", "[size]" => "{size}", _ => p[2..] }));
        Check.Equal(System.Text.RegularExpressions.Regex.Replace(sentence, @"\s+", " "), System.Text.RegularExpressions.Regex.Replace(joined, @"\s+", " "), "nothing lost, nothing added");
    }

    [UnitTest]
    public static void The_archive_rule_key_is_seen_by_the_source_scan()
    {
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        var hits = L10nSourceScan.CSharp(root, new[] { "MainWindow.Language.cs" }, new[] { L10nSourceScan.T });
        Check.True(hits.Any(h => h.Key == MainWindow.ArchiveRuleEnglish), "L.T(\"" + MainWindow.ArchiveRuleEnglish + "\") is found: " + string.Join(" | ", hits.Select(h => h.Key)));
        var xaml = File.ReadAllText(Path.Combine(root, "MainWindow.xaml"));
        Check.False(xaml.Contains("days, or when the local folder exceeds", StringComparison.Ordinal), "the XAML has no fragment of it");
    }
}