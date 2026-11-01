using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clink.Screenshots;
using Clink.Native;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Screenshots.Sources;
using Clink.Services.Localization;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>
/// Localization guard for the screenshot editor area (slice S4): under the pseudo-locale every user-visible string of the editor window,
/// its toolbar (full and compact), the Adjust flyout, the insert-image picker (with and without history) and the texts the code sets
/// (status line, warnings, mode labels, errors) must come from the catalog, and no key may be missing from Locales/en/editor.json.
/// Everything is built off screen and never shown.
/// </summary>
internal static class L10nTreeEditorTests
{
    // A tile's tooltip is the screenshot's file name: user data, not translatable.
    private static bool Allow(string text) => text.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static EditorRequest Request(string title, string? hint = null) => new()
    {
        Document = DocumentState.Create(new BgraImage(200, 150)),
        Title = title,
        Hint = hint,
        IsNewCapture = true,
        Options = new ScreenshotEditorOptions(),
        SaveAsync = (_, _, _) => Task.FromResult(true),
    };

    private static T Find<T>(DependencyObject root, string name) where T : class =>
        (LogicalTreeHelper.FindLogicalNode(root, name) as T) ?? throw new InvalidOperationException("no element named " + name);

    private sealed class NoPictureClipboard : IImageClipboard
    {
        public BgraImage? GetImage(out string? error) { error = null; return null; }
    }

    [UnitTest]
    public static void The_editor_window_has_no_hard_coded_text_with_every_tool_selected_and_the_warnings_showing()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var host = new EditorWindowHost { ShowWindows = false };
        var open = host.EditAsync(Request(L.T("New screenshot"), ScreenshotService.SpanHint));
        var window = host.Window!;
        try
        {
            var session = window.Session!;
            foreach (EditorTool tool in Enum.GetValues<EditorTool>()) session.SetTool(tool);   // every option panel is refreshed for its tool
            session.SetTool(EditorTool.Select);
            var quiet = L10nTreeHarness.Unlocalized(window, Allow);
            Check.Equal(Array.Empty<string>(), quiet.ToArray(), "the editor window at rest");

            // the 50-object warning goes to the status line
            for (int i = 0; i < EditorSession.RasterWarnCount; i++) session.Add(new HighlightRect(0, new RectI(i % 100, i % 20, 5, 5), Colors.Yellow));
            var status = Find<TextBlock>(window, "StatusText");
            Check.True(PseudoLocale.IsPseudo(status.Text), "the warning comes from the catalog: " + status.Text);
            Check.Contains(status.Text, "50");

            // the refusals of an insert go to the status line as well
            session.Clipboard = new NoPictureClipboard();
            var paste = session.PasteImageAsync(new Point(10, 10));
            ScreenshotUploadTests.Pump(() => paste.IsCompleted, "the paste");
            Check.False(paste.Result.Ok);
            Check.True(PseudoLocale.IsPseudo(status.Text), "the paste refusal: " + status.Text);
            var missing = session.InsertImageFileAsync(@"C:\definitely\not\here.png", new Point(10, 10));
            ScreenshotUploadTests.Pump(() => missing.IsCompleted, "the file insert");
            Check.False(missing.Result.Ok);
            Check.True(PseudoLocale.IsPseudo(status.Text), "the file refusal: " + status.Text);

            var after = L10nTreeHarness.Unlocalized(window, Allow);
            Check.Equal(Array.Empty<string>(), after.ToArray(), "the editor window with a status message");
            Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in Locales/en/editor.json");
        }
        finally
        {
            window.CloseApproved();
            ScreenshotUploadTests.Pump(() => open.IsCompleted, "the window to close");
        }
    }

    [UnitTest]
    public static void The_toolbar_full_and_compact_and_the_adjust_flyout_have_no_hard_coded_text()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var session = new EditorSession(DocumentState.Create(new BgraImage(120, 90)));
        try
        {
            var toolbar = new EditorToolbar();
            toolbar.Attach(session);
            toolbar.SetZoom(1.5);
            foreach (EditorTool tool in Enum.GetValues<EditorTool>()) session.SetTool(tool);
            Check.Equal(Array.Empty<string>(), L10nTreeHarness.Unlocalized(toolbar).ToArray(), "the full toolbar");
            toolbar.Compact = true;   // the capture overlay's strip: Cancel and Done instead of zoom / Copy / Save
            Check.Equal(Array.Empty<string>(), L10nTreeHarness.Unlocalized(toolbar).ToArray(), "the compact toolbar");

            // the Adjust flyout (a Popup: its content is a logical child) with a non-identity value
            session.SetAdjust(new AdjustmentValues(25, -10, 40), final: true);
            Check.True(toolbar.AdjustPopup.Child != null, "the flyout exists");
            Check.Equal(Array.Empty<string>(), L10nTreeHarness.Unlocalized(toolbar.AdjustPopup).ToArray(), "the Adjust flyout");

            // tooltips that carry a key name keep it verbatim inside the catalog text
            string undoTip = (string)Find<Button>(toolbar, "UndoButton").ToolTip;
            Check.True(PseudoLocale.IsPseudo(undoTip) && undoTip.Contains("Ctrl+Z"), "the key name is a placeholder value: " + undoTip);
            Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in Locales/en/editor.json");
        }
        finally { session.Release(); }
    }

    private static void RunLoad(InsertImagePicker picker)
    {
        // Loaded is not raised for a window that is never shown: start the load the way the event would
        var load = typeof(InsertImagePicker).GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)load.Invoke(picker, null)!;
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the picker to load");
    }

    [UnitTest]
    public static void The_insert_image_picker_has_no_hard_coded_text_with_and_without_history()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();

        var empty = new InsertImagePicker(new InsertPickerModel(null), canPaste: true);
        RunLoad(empty);
        Check.Equal(Visibility.Visible, Find<TextBlock>(empty, "EmptyText").Visibility, "no history: the empty line shows");
        Check.Equal(Array.Empty<string>(), L10nTreeHarness.Unlocalized(empty, Allow).ToArray(), "the picker without history");

        var entries = new List<ScreenshotEntry>
        {
            new() { Path = @"C:\shots\one.png", Created = new DateTime(2026, 3, 5, 13, 30, 0), Width = 1280, Height = 720 },
            new() { Path = @"C:\shots\two.png", Created = new DateTime(2026, 3, 6, 9, 5, 0), Width = 640, Height = 480 },
        };
        var model = new InsertPickerModel(() => (entries, Array.Empty<string>()), fileOk: _ => true, decode: _ => null);
        var full = new InsertImagePicker(model, canPaste: false);
        RunLoad(full);
        var tiles = Find<WrapPanel>(full, "Tiles");
        Check.Equal(2, tiles.Children.Count, "a tile per recent screenshot");
        Check.Equal(Visibility.Collapsed, Find<TextBlock>(full, "EmptyText").Visibility);
        Check.Equal(Array.Empty<string>(), L10nTreeHarness.Unlocalized(full, Allow).ToArray(), "the picker with history (captions, tile names)");
        string name = System.Windows.Automation.AutomationProperties.GetName(tiles.Children[0]);
        Check.True(PseudoLocale.IsPseudo(name) && name.Contains("one.png"), "the tile's automation name is a template around the file name: " + name);
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in Locales/en/editor.json");
    }

    [UnitTest]
    public static void Texts_the_code_sets_come_from_the_catalog()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var all = new List<string>
        {
            ScreenshotService.SpanHint, ScreenshotService.DisabledText,
            ComposeQueue.RetryingNotice, ComposeQueue.GaveUpNotice, ImageInput.TooLarge, ImageInput.Unreadable,
            ScreenshotService.Subtitle(new ScreenshotEntry { Width = 1280, Height = 720 }, new ScreenshotToastState(true, false)),
            ScreenshotService.Subtitle(new ScreenshotEntry { Width = 1280, Height = 720 }, new ScreenshotToastState(false, true)),
            ScreenshotService.Subtitle(new ScreenshotEntry { Width = 1280, Height = 720 }, new ScreenshotToastState(false, false)),
            RegionOverlayPicker.IdleCancelledText(TimeSpan.FromMinutes(2)),
            RegionOverlayPicker.IdleCancelledText(TimeSpan.FromMinutes(20)),
            SelectionMath.SizeLabel(new RectI(5, 5, 1280, 720)),
            EffectsWarning(),
        };
        foreach (string text in all) Check.True(PseudoLocale.IsPseudo(text), "not from the catalog: " + text);
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in Locales/en/editor.json");
    }

    /// <summary>The "N effects on this picture" warning, as the session itself raises it (50 raster objects).</summary>
    private static string EffectsWarning()
    {
        var session = new EditorSession(DocumentState.Create(new BgraImage(200, 150)));
        try
        {
            string? warning = null;
            session.Notice += text => warning ??= text;
            for (int i = 0; i < EditorSession.RasterWarnCount; i++) session.Add(new HighlightRect(0, new RectI(i % 100, i % 20, 5, 5), Colors.Yellow));
            return warning ?? throw new InvalidOperationException("the session raised no warning");
        }
        finally { session.Release(); }
    }

    [UnitTest]
    public static void The_after_capture_choices_stay_english_catalog_keys_for_the_settings_tab_to_translate()
    {
        // MainWindow translates label and tip with L.T when it shows them (their entries are in Locales/en/main.json): they must not be translated here too
        using var scope = L.Scope("qps-ploc");
        var choices = ScreenshotModes.Choices;
        Check.Equal(new[] { "Save only", "Open editor", "Edit on screen" }, choices.Select(c => c.Label).ToArray());
        Check.True(choices.All(c => !PseudoLocale.IsPseudo(c.Label) && !PseudoLocale.IsPseudo(c.Tip)), "untranslated keys");
        Check.Equal("Save the picture at once; the notification has an Edit button", choices[0].Tip);
        Check.Equal("Open the editor on the picture first; Done saves it", choices[1].Tip);
    }

    [UnitTest]
    public static void The_english_texts_of_the_editor_are_what_they_were()
    {
        // English stays byte-identical: spot checks of the templates that were assembled from pieces before
        using var scope = L.Scope("en");
        L.ClearMissing();
        Check.Equal("Undo (Ctrl+Z)", L.T("Undo ({keys})", ("keys", L.Ltr("Ctrl+Z"))));
        Check.Equal("4 px", L.T("unit|{n} px", ("n", Math.Round(4.0))));
        Check.Equal("2.5x", L.T("factor|{n}x", ("n", 2.5.ToString("0.##", L.FormatCulture))));
        Check.Equal("100%", L.T("unit|{n}%", ("n", Math.Round(100.0))));
        Check.Equal("1280 x 720", SelectionMath.SizeLabel(new RectI(5, 5, 1280, 720)));
        Check.Equal("1280 x 720 · copied to clipboard", ScreenshotService.Subtitle(new ScreenshotEntry { Width = 1280, Height = 720 }, new ScreenshotToastState(true, false)));
        Check.Equal("1280 x 720 · clipboard busy, not copied", ScreenshotService.Subtitle(new ScreenshotEntry { Width = 1280, Height = 720 }, new ScreenshotToastState(false, true)));
        Check.Equal("1280 x 720", ScreenshotService.Subtitle(new ScreenshotEntry { Width = 1280, Height = 720 }, new ScreenshotToastState(false, false)));
        Check.Equal("Block", L.T("pixelate|Block"));
        Check.Equal("Dim", L.T("spotlight|Dim"));
        Check.Equal("Fit", L.T("zoom|Fit"));
        Check.Equal("Source", L.T("magnify|Source"));
        Check.Equal("Blur", L.T("spotlight|Blur"));
        Check.Equal("Outline", L.T("magnify|Outline"));
        Check.Equal("50 effects on this picture: more of them make editing slower. Consider flattening by saving and reopening.", EffectsWarning());
        Check.Equal("Screenshot cancelled after 2 minutes without input", RegionOverlayPicker.IdleCancelledText(TimeSpan.FromMinutes(2)));
        Check.Equal("Screenshot cancelled after 20 minutes without input", RegionOverlayPicker.IdleCancelledText(TimeSpan.FromMinutes(20)));
        Check.Equal("Screenshot cancelled after 1 minute without input", RegionOverlayPicker.IdleCancelledText(TimeSpan.FromMinutes(1)));
        Check.Equal("Saved locally. The picture is 2.1 MB, over your Copium upload limit of 1 MB.",
            L.T("Saved locally. The picture is {size}, over your Copium upload limit of {limit}.", ("size", L.Bytes(2_100_000)), ("limit", L.Bytes(1_000_000))));
        Check.Equal("Insert one.png", L.T("Insert {name}", ("name", L.Ltr("one.png"))));
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray());
    }

    // ------------------------------------------------------------------ the three reasons an upload is skipped (gone / changed / check failed)

    [UnitTest]
    public static void The_english_texts_of_the_file_check_before_an_upload_are_what_they_were()
    {
        using var scope = L.Scope("en");
        L.ClearMissing();
        using var dir = new TempDir();
        string file = dir.File("one.png");
        File.WriteAllBytes(file, new byte[100]);

        var gone = ScreenshotService.ChangedSinceSaved(new ScreenshotEntry { Path = dir.File("none.png"), Size = 100 })!;
        Check.Equal("the file is gone", gone.Log);
        Check.Equal("Saved locally, but it was not uploaded: the file is gone.", gone.Toast);
        Check.Equal("The file is gone", gone.Batch);

        var changed = ScreenshotService.ChangedSinceSaved(new ScreenshotEntry { Path = file, Size = 50 })!;
        Check.Equal("the file changed after it was saved", changed.Log);
        Check.Equal("Saved locally, but it was not uploaded: the file changed after it was saved.", changed.Toast);
        Check.Equal("The file changed after it was saved", changed.Batch);

        Check.True(ScreenshotService.ChangedSinceSaved(new ScreenshotEntry { Path = file, Size = 100 }) == null, "an untouched file passes");

        var failed = ScreenshotService.ChangedSinceSaved(new ScreenshotEntry { Path = file, Size = 100 }, _ => throw new IOException("disk on fire"))!;
        Check.Equal("the file could not be checked (disk on fire)", failed.Log);
        Check.Equal("Saved locally, but it was not uploaded: the file could not be checked (disk on fire).", failed.Toast);
        Check.Equal("The file could not be checked (disk on fire)", failed.Batch);
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in Locales/en/editor.json");
    }

    [UnitTest]
    public static void A_changed_file_is_reported_in_the_user_language_in_the_toast_and_the_batch_line_and_in_english_in_the_log()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        using var dir = new TempDir();
        var rig = ScreenshotUploadTests.Make(dir, upload: false);
        ScreenshotUploadTests.Capture(rig);
        var entry = rig.Toasts.LastEntry!;
        File.AppendAllText(entry.Path, "x");   // the size differs
        string? batchText = null;
        Log.Line += Collect;
        try
        {
            var one = rig.Service.UploadAsync(entry, manual: true);
            ScreenshotUploadTests.Pump(() => one.IsCompleted, "the check");
            var batch = rig.Service.UploadAsync(entry, manual: true, batch: true, batchProblem: s => batchText = s);
            ScreenshotUploadTests.Pump(() => batch.IsCompleted, "the batch check");
        }
        finally { Log.Line -= Collect; }
        Check.Equal(0, rig.Calls.Count, "nothing was sent");
        Check.True(PseudoLocale.IsPseudo(rig.Toasts.LastProblemText), "the toast text: " + rig.Toasts.LastProblemText);
        Check.True(batchText != null && PseudoLocale.IsPseudo(batchText), "the batch line: " + batchText);
        string[] logged;
        lock (lines) logged = lines.Where(l => l.Contains("upload skipped for", StringComparison.Ordinal)).ToArray();
        Check.Equal(2, logged.Length);
        Check.True(logged.All(l => l.EndsWith(": the file changed after it was saved", StringComparison.Ordinal) && !PseudoLocale.IsPseudo(l)), "the log stays English: " + string.Join(" | ", logged));
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in Locales/en/editor.json");
    }

    // ------------------------------------------------------------------ the capture failures behind "Screenshot not taken"

    private sealed class NobodyHandles : IScreenshotSource
    {
        public string Name => "none";
        public bool Handles(MonitorInfo monitor) => false;
        public FrozenMonitor Capture(MonitorInfo monitor) => throw new InvalidOperationException("not called");
    }

    private static readonly MonitorInfo Display1 = new(0, @"\\.\DISPLAY1", "", 0, 0, 64, 48, true, false, 80, 0, "");

    [UnitTest]
    public static void Capture_failures_keep_an_english_message_for_the_log_and_give_the_user_text_in_the_user_language()
    {
        var broken = new MonitorInfo(0, @"\\.\DISPLAY1", "", 0, 0, 0, 0, true, false, 80, 0, "");
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            using var scope = L.Scope(locale);
            bool pseudo = locale != "en";
            L.ClearMissing();
            var cases = new (Func<Exception?> Run, string English)[]
            {
                (() => Catch(() => new GdiScreenshotSource().Capture(broken)), "DISPLAY1 has an unusable size (0 x 0)"),
                (() => Catch(() => FrozenDesktop.CaptureAll(Array.Empty<MonitorInfo>(), new IScreenshotSource[] { new NobodyHandles() })), "No monitor was found"),
                (() => Catch(() => FrozenDesktop.CaptureAll(new[] { Display1 }, new IScreenshotSource[] { new NobodyHandles() })), "No screenshot source handles DISPLAY1"),
            };
            foreach (var (run, english) in cases)
            {
                var ex = (ScreenshotCaptureException)run()!;
                Check.Equal(english, ex.Message, "the log message is English under " + locale);
                if (pseudo) Check.True(PseudoLocale.IsPseudo(ex.UserText), "the user text comes from the catalog: " + ex.UserText);
                else Check.Equal(english, ex.UserText, "English user text = today's toast text");
            }
            Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in Locales/en/editor.json");
        }

        // a thrower with no user text of its own shows its message
        Check.Equal("plain", new ScreenshotCaptureException("plain").UserText);

        // the API detail is a placeholder value, never translated
        using (L.Scope("qps-ploc"))
        {
            string text = L.T("The screen could not be read ({detail})", ("detail", "GetDC failed"));
            Check.True(PseudoLocale.IsPseudo(text) && text.Contains("GetDC failed"), text);
        }
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }

    [UnitTest]
    public static void The_screenshot_not_taken_toast_shows_the_user_text_and_the_log_the_english_message()
    {
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            using var scope = L.Scope(locale);
            using var dir = new TempDir();
            var rig = ScreenshotUploadTests.Make(dir);
            var service = new ScreenshotService(rig.Settings, rig.Toasts, new ScreenshotUploadTests.OnePicker(), () => null, () => null, new IScreenshotSource[] { new NobodyHandles() }, () => new[] { Display1 }, null);
            lock (lines) lines.Clear();
            Log.Line += Collect;
            try
            {
                var t = service.CaptureRegionAsync(ScreenshotTrigger.Hotkey);
                ScreenshotUploadTests.Pump(() => t.IsCompleted, "the failed capture");
            }
            finally { Log.Line -= Collect; }
            Check.Equal(1, rig.Toasts.Shown.Count, "one toast under " + locale);
            if (locale == "en") Check.Equal("No screenshot source handles DISPLAY1", rig.Toasts.LastProblemText);
            else Check.True(PseudoLocale.IsPseudo(rig.Toasts.LastProblemText), "the toast body: " + rig.Toasts.LastProblemText);
            string[] logged;
            lock (lines) logged = lines.Where(l => l.Contains("No screenshot source handles", StringComparison.Ordinal)).ToArray();
            Check.Equal(1, logged.Length);
            Check.True(logged[0].EndsWith("screenshot: No screenshot source handles DISPLAY1", StringComparison.Ordinal) && !PseudoLocale.IsPseudo(logged[0]), "the log stays English: " + logged[0]);
        }
    }

    // ------------------------------------------------------------------ the keys the slice's sources ask for exist in the English catalog

    private static string? FindSourceFolder()
    {
        // (the same way the main slice's test finds them: the tests run from a checkout's build output; elsewhere there is nothing to read)
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Clink.csproj"))) return dir.FullName;
        return null;
    }

    [UnitTest]
    public static void Every_key_in_the_slice_sources_is_in_the_English_catalog()
    {
        string? root = FindSourceFolder();
        if (root == null) return;   // not run from a checkout: nothing to read
        var english = L.Catalogs.English.Entries;
        var problems = new List<string>();
        int keys = 0;

        // C#: the first string literal of L.T( / L.Plural( and of the toolbar's Tip( helper
        var code = new Regex(@"(?:\bL\.(?:T|Plural)|\bTip)\(\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        foreach (string file in new[]
        {
            @"Screenshots\Editing\ComposeQueue.cs", @"Screenshots\Editing\ImageInput.cs", @"Screenshots\ScreenshotService.cs",
            @"Screenshots\Sources\GdiScreenshotSource.cs", @"Screenshots\Sources\FrozenDesktop.cs", @"Screenshots\Geometry\SelectionMath.cs",
            @"UI\Screenshots\EditorCanvas.cs", @"UI\Screenshots\EditorSession.cs", @"UI\Screenshots\EditorToolbar.xaml.cs",
            @"UI\Screenshots\InsertImagePicker.xaml.cs", @"UI\Screenshots\RegionOverlayWindow.xaml.cs", @"UI\Screenshots\ScreenshotEditorWindow.xaml.cs",
        })
            foreach (Match m in code.Matches(File.ReadAllText(Path.Combine(root, file))))
            {
                string key = Unescape(m.Groups[1].Value);
                keys++;
                if (!english.ContainsKey(key)) problems.Add(file + ": " + key);
            }

        // XAML: {l:T '...'} (an apostrophe is \', a backslash \\; the attribute is XML-escaped)
        var xaml = new Regex(@"\{l:T '((?:[^'\\]|\\.)*)'\}", RegexOptions.Compiled);
        foreach (string file in new[]
        {
            @"UI\Screenshots\EditorToolbar.xaml", @"UI\Screenshots\InsertImagePicker.xaml", @"UI\Screenshots\RegionOverlayWindow.xaml", @"UI\Screenshots\ScreenshotEditorWindow.xaml",
        })
            foreach (Match m in xaml.Matches(File.ReadAllText(Path.Combine(root, file))))
            {
                string key = Unescape(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
                keys++;
                if (!english.ContainsKey(key)) problems.Add(file + ": " + key);
            }

        Check.True(keys > 150, "found the keys of the slice's sources: " + keys);
        Check.Equal(Array.Empty<string>(), problems.ToArray(), "keys missing from Locales/en");
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\'", "'").Replace("\\\\", "\\");
}
