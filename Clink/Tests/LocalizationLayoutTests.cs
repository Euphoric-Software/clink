using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Clink.Capture;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>
/// Slice S6 phase B, layout: every window is laid out off screen at its own width under the pseudo languages (qps-ploc: about 40 % longer
/// text; qps-plocm: the same, right-to-left) and every element that is given less room than it needs (WPF clips it, LayoutInformation.GetLayoutClip)
/// is reported with the window, the element and the ratio needed / given. Nothing is shown. English must keep its geometry: a single row
/// where English has one (the toast's buttons).
/// </summary>
internal static class LocalizationLayoutTests
{
    /// <summary>One window to lay out: a name, the window, the width of its content and the height (0: as tall as the content wants).</summary>
    private sealed record Case(string Name, Window Window, double Width, double Height);

    private static AppController NewApp()
    {
        var settings = Settings.Load();   // the runner's scratch folder
        settings.CaptureBorderNoticeDismissed = true;
        return new AppController(settings, new Updater(settings));
    }

    private static ClipEntry SampleClip(bool shared = false) =>
        new() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-l10n-nonexistent", "Neon Drift 2026-10-20 21-04-11.mp4"), Game = "Neon Drift", Duration = 90, Size = 190_000_000, Created = DateTime.Now, CopiumLink = shared ? "https://copium.pics/v/abc" : null };

    private static ScreenshotEntry SampleShot(string? link = null) =>
        new() { Path = @"C:\Shots\Screenshot 2026-10-20 21-04-11.png", Width = 2560, Height = 1440, Size = 1_200_000, Created = DateTime.Now, CopiumLink = link, CopiumPageUrl = link };

    private static ToastWindow Toast(string kind, string title, string subtitle, ClipEntry? entry = null, string? link = null, ScreenshotEntry? shot = null, bool canUpload = false) =>
        new(null!, kind, entry, link, title, subtitle, TimeSpan.FromSeconds(7), false, shot, canUpload);

    /// <summary>
    /// What has less room than it needs, as "Window: path (what; needed / given = ratio)". Three kinds: an element WPF clipped because its slot was
    /// smaller than it asked for (LayoutInformation.GetLayoutClip); an element that sticks out of the window sideways (a row of controls wider than the
    /// window: WPF does not clip those); an unwrapped text without an ellipsis that is wider than the room it was arranged in (it is drawn over its neighbour).
    /// </summary>
    internal static List<string> Clipped(string name, FrameworkElement root, double width, double height)
    {
        var found = new List<string>();
        root.Measure(new Size(width, height > 0 ? height : double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, width, height > 0 ? height : root.DesiredSize.Height));
        root.UpdateLayout();
        var texts = new List<(string Path, TextBlock Text, double Given)>();
        Walk(root, root, name, width, found, new HashSet<DependencyObject>(), texts, clippedAbove: false, index: 0);
        // natural width of the unwrapped texts: measured with all the room in the world, after the checks above (it disturbs the layout)
        foreach (var (path, tb, given) in texts)
        {
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double natural = tb.DesiredSize.Width;
            if (given > 0 && natural > given + 1) found.Add($"{path} <text wider than its room> | needs {natural:0} of {given:0} = {natural / given:0.00} \"{Cut(tb.Text)}\"");
        }
        return found;
    }

    private static void Walk(DependencyObject node, FrameworkElement root, string path, double rootWidth, List<string> found, HashSet<DependencyObject> seen,
        List<(string, TextBlock, double)> texts, bool clippedAbove, int index)
    {
        if (!seen.Add(node)) return;
        if (node is FrameworkElement fe)
        {
            if (fe.Visibility != Visibility.Visible) return;
            try { fe.ApplyTemplate(); } catch { }
            path += "/" + fe.GetType().Name + "[" + index + "]" + (fe.Name.Length > 0 ? "#" + fe.Name : "");   // the child index keeps two siblings of one type apart
            string label = fe is TextBlock tb0 ? " \"" + Cut(tb0.Text) + "\"" : fe is ContentControl { Content: string s } ? " \"" + Cut(s) + "\"" : "";
            var slot = LayoutInformation.GetLayoutSlot(fe);
            double needed = fe.DesiredSize.Width - fe.Margin.Left - fe.Margin.Right;
            double given = slot.Width - fe.Margin.Left - fe.Margin.Right;
            if (LayoutInformation.GetLayoutClip(fe) != null && given > 0 && needed > given + 0.5 && Reportable(fe))
                found.Add($"{path} <clipped by its slot> | needs {needed:0} of {given:0} = {needed / given:0.00}{label}");
            else if (!clippedAbove && fe != root && fe.ActualWidth > 0 && fe.IsDescendantOf(root) && Reportable(fe) && fe is TextBlock or ButtonBase or TextBox or ComboBox)
            {
                var r = fe.TransformToAncestor(root).TransformBounds(new Rect(fe.RenderSize));
                if (r.Right > rootWidth + 1 || r.Left < -1)
                    found.Add($"{path} <sticks out of the window> | spans {r.Left:0}..{r.Right:0} of {rootWidth:0} = {Math.Max(r.Right, rootWidth) / rootWidth:0.00}{label}");
            }
            if (fe is TextBlock { TextWrapping: TextWrapping.NoWrap, TextTrimming: TextTrimming.None } text && text.Text.Length > 0 && text.ActualWidth > 0 && !text.Text.Any(c => c is >= '\uE000' and <= '\uF8FF'))
                texts.Add((path, text, text.ActualWidth));
            clippedAbove |= fe.ClipToBounds;
        }
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(node); i < n; i++) Walk(VisualTreeHelper.GetChild(node, i), root, path, rootWidth, found, seen, texts, clippedAbove, i);
    }
    private static string Cut(string s) => s.Length <= 50 ? s : s[..50] + "…";

    /// <summary>A clip that is the design (a scrolling panel, a trimmed text) is not a finding.</summary>
    private static bool Reportable(FrameworkElement fe) =>
        fe is not (ScrollViewer or ScrollContentPresenter) && !(fe is TextBlock { TextTrimming: not TextTrimming.None }) && !(fe is TextBlock { TextWrapping: TextWrapping.Wrap });

    private static void Mirror(Window w)
    {
        if (L.IsRtl) RtlWindows.ApplyTo(w);
    }

    private static List<Case> Windows(AppController app, Func<Window, Window> prepare)
    {
        var cases = new List<Case>();
        var main = new MainWindow();
        typeof(MainWindow).GetMethod("Populate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
        cases.Add(new("MainWindow", prepare(main), 470, 780));
        cases.Add(new("ArchiveWindow", prepare(new ArchiveWindow(app)), 500, 0));
        cases.Add(new("ClipDeleteConfirmWindow", prepare(new ClipDeleteConfirmWindow(ClipDeleter.ConfirmBodyWithTrims("Neon Drift", 2, shared: true), null, 2, null)), 400, 0));
        cases.Add(new("ClipDeleteConfirmWindow (bulk)", prepare(new ClipDeleteConfirmWindow(ClipDeleter.BulkConfirmTitle(3, false), ClipDeleter.BulkConfirmBody(3, 1, 2, 1, false), L.T("Delete"), null)), 400, 0));
        cases.Add(new("CopiumConsentWindow", prepare(new CopiumConsentWindow(CopiumConsentWindow.Mode.Consent, offerAutoUpload: true, excludeFromCapture: false)), 460, 0));
        cases.Add(new("CopiumConsentWindow (cloud)", prepare(new CopiumConsentWindow(CopiumConsentWindow.Mode.ConfirmCloudSharing, offerAutoUpload: true, excludeFromCapture: false)), 460, 0));
        cases.Add(new("CopiumWelcomeWindow", prepare(new CopiumWelcomeWindow(new CopiumWelcomeModel(true, (_, _) => { }), "Jason", () => false)), 480, 0));
        cases.Add(new("ShareWindow", prepare(new ShareWindow(SampleClip())), 480, 0));
        cases.Add(new("ShareFilesWindow", prepare(new ShareFilesWindow(new[] { new ShareFileItem(@"C:\Clips\x.mp4", false, 1000, 1), new ShareFileItem(@"C:\Clips\y.mp4", false, 1000, 1) }, false)), 480, 0));
        cases.Add(new("ThirdPartyNoticesWindow", prepare(new ThirdPartyNoticesWindow()), 780, 580));
        cases.Add(new("InsertImagePicker", prepare(new InsertImagePicker(new InsertPickerModel(null), canPaste: true)), 640, 480));
        cases.Add(new("EditorToolbar", prepare(new Window { Content = new EditorToolbar() }), 1100, 0));
        string tokenless = L.T("Clip saved");
        cases.Add(new("Toast (clip)", prepare(Toast("clip", tokenless, Toasts.ClipSubtitle(SampleClip()), SampleClip())), 330, 0));
        cases.Add(new("Toast (clip, linked)", prepare(Toast("clip", tokenless, Toasts.ClipSubtitle(SampleClip(true)), SampleClip(true))), 330, 0));
        cases.Add(new("Toast (shared)", prepare(Toast("shared", L.T("Shared · link copied"), "https://copium.pics/v/abc", SampleClip(true), "https://copium.pics/v/abc")), 330, 0));
        cases.Add(new("Toast (screenshot)", prepare(Toast("screenshot", L.T("Screenshot saved"), "2560×1440 · copied to clipboard", null, null, SampleShot(), true)), 330, 0));
        cases.Add(new("Toast (screenshot, linked)", prepare(Toast("screenshot", L.T("Screenshot saved"), "2560×1440 · copied to clipboard", null, "https://copium.pics/s/abc", SampleShot("https://copium.pics/s/abc"), true)), 330, 0));
        cases.Add(new("Toast (problem)", prepare(Toast("problem", L.T("Clip not saved"), L.T("Not recording: waiting for a game. If your game is not detected, add it under Capture → Recording → Custom apps."))), 330, 0));
        var (borderTitle, borderText) = AppController.BorderNoticeTexts(CaptureBorderState.Denied);
        cases.Add(new("Toast (border notice)", prepare(Toast("border", borderTitle, borderText)), 330, 0));
        cases.Add(new("Toast (record)", prepare(Toast("record", L.T("Recording started"), L.T("Press {keys} to stop", ("keys", L.Ltr("Ctrl+F9"))))), 330, 0));
        return cases;
    }

    /// <summary>Every window of <see cref="Windows"/> under <paramref name="language"/>, each tab of the main window, the trim window at its smallest and its default width.</summary>
    private static List<string> Everything(string language)
    {
        var found = new List<string>();
        using var rig = new TrimShareSplitTests.Rig(true, true);
        using (L.Scope(language))
        {
            foreach (var c in Windows(rig.App, w => { Mirror(w); return w; }))
            {
                var root = (FrameworkElement)c.Window.Content;
                double width = c.Window is MainWindow or ToastWindow or ArchiveWindow or ClipDeleteConfirmWindow or CopiumConsentWindow or CopiumWelcomeWindow or ShareWindow or ShareFilesWindow or InsertImagePicker or ThirdPartyNoticesWindow && !double.IsNaN(c.Window.Width) ? c.Window.Width : c.Width;
                if (c.Window is MainWindow main)
                {
                    int index = 0;
                    foreach (var tab in main.Tabs.Items.OfType<TabItem>())
                    {
                        main.Tabs.SelectedItem = tab;
                        found.AddRange(Clipped(c.Name + " [tab " + index++ + "]", root, width, c.Height));
                    }
                }
                else found.AddRange(Clipped(c.Name, root, width, c.Height));
            }
            using var win = new TrimShareSplitTests.Win(rig.Entry);
            Mirror(win.Window);
            foreach (double width in new[] { 720.0, 940.0 })
                found.AddRange(Clipped("TrimWindow @" + width, (FrameworkElement)win.Window.Content, width, 720));
        }
        return found.Distinct().ToList();
    }

    private static string Key(string finding) { int at = finding.IndexOf(" | ", StringComparison.Ordinal); return at < 0 ? finding : finding[..at]; }

    /// <summary>What the language clips that English does not: English has a few places that are tight today (the version text, the wordmark, the four-button delete row); those are not the language's doing.</summary>
    private static List<string> OnlyIn(string language)
    {
        var english = Everything("en").Select(Key).ToHashSet();
        return Everything(language).Where(f => !english.Contains(Key(f))).ToList();
    }

    [UnitTest]
    public static void Under_the_pseudo_language_no_window_clips_its_text()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var found = OnlyIn(L.PseudoCode);
        Check.Equal(Array.Empty<string>(), found.ToArray(), "clipped under " + L.PseudoCode + " and not in English (" + found.Count + ")");
    }

    [UnitTest]
    public static void Under_the_pseudo_right_to_left_language_no_window_clips_its_text()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var found = OnlyIn(L.PseudoRtlCode);
        Check.Equal(Array.Empty<string>(), found.ToArray(), "clipped under " + L.PseudoRtlCode + " and not in English (" + found.Count + ")");
    }

    [UnitTest]
    public static void The_replay_length_slider_keeps_its_English_geometry_and_makes_room_for_a_longer_language()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        double english, pseudo;
        using (L.Scope("en")) english = new ReplayLengthSlider().LabelWidthPx;
        using (L.Scope(L.PseudoCode)) pseudo = new ReplayLengthSlider().LabelWidthPx;
        Check.Equal(50.0, english, "English: the label keeps its 50 pixels");
        Check.True(pseudo > 50, "a language with longer units gets more room: " + pseudo);
        // the tick captions at the width the main window gives the slider: all of them in English (as before), and none runs into another under a long language
        var english1 = DrawnCaptions("en");
        Check.Equal(8, english1.Count, "English draws every tick caption (15 s, 30 s, 1 m, 2 m, 5 m, 10 m, 30 m, 1 h)");
        foreach (string language in new[] { L.PseudoCode, L.PseudoRtlCode })
        {
            var drawn = DrawnCaptions(language);
            Check.True(drawn.Count >= 2, language + ": at least the first and the last caption stay: " + drawn.Count);
            for (int i = 1; i < drawn.Count; i++)
                Check.True(drawn[i].X >= drawn[i - 1].X + drawn[i - 1].Width, language + ": caption " + i + " starts at " + drawn[i].X.ToString("0.0") + ", the one before ends at " + (drawn[i - 1].X + drawn[i - 1].Width).ToString("0.0"));
        }
    }

    /// <summary>The captions the replay-length slider draws in the main window (laid out at its own width, rendered offscreen: nothing is shown) under <paramref name="language"/>, left to right.</summary>
    private static List<(double X, double Width)> DrawnCaptions(string language)
    {
        using var rig = new TrimShareSplitTests.Rig(true, true);
        using (L.Scope(language))
        {
            var main = new MainWindow();
            typeof(MainWindow).GetMethod("Populate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
            Mirror(main);
            var root = (FrameworkElement)main.Content;
            double width = double.IsNaN(main.Width) ? 470 : main.Width, height = 780;
            foreach (var tab in main.Tabs.Items.OfType<TabItem>())
            {
                main.Tabs.SelectedItem = tab;
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                root.UpdateLayout();
                var slider = Descendants(root).OfType<ReplayLengthSlider>().FirstOrDefault(s => s.ActualWidth > 0);
                if (slider == null) continue;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);   // draws OnRender, nothing on screen
                return slider.DrawnCaptions.OrderBy(c => c.X).ToList();
            }
        }
        throw new TestFailure("no replay-length slider in the main window");
    }
    [UnitTest]
    public static void English_findings_are_the_known_tight_spots_only()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var english = Everything("en");
        // by element, not by window: the version text and the status line of the main window (every tab), the trim window's position text; nothing of the delete question, the toasts or any other window
        static bool Known(string key) =>
            key.StartsWith("MainWindow [tab ", StringComparison.Ordinal) && (key.EndsWith("/StackPanel[1]/InvokableTextBlock[1]#VersionText <text wider than its room>", StringComparison.Ordinal) || key.EndsWith("/Grid[2]#StatusHost/StackPanel[0]#StatusLead/TextBlock[1] <text wider than its room>", StringComparison.Ordinal))
            || key.StartsWith("TrimWindow @", StringComparison.Ordinal) && key.EndsWith("/Grid[4]/TextBlock[2]#PosText <text wider than its room>", StringComparison.Ordinal);
        Check.Equal(Array.Empty<string>(), english.Select(Key).Distinct().Where(k => !Known(k)).ToArray(), "English findings beyond the known tight spots");
    }
    [UnitTest]
    public static void The_layout_check_finds_a_text_that_has_less_room_than_it_needs_and_passes_one_that_wraps_or_trims()
    {
        var clipped = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Button { Content = "A long caption that cannot fit", Name = "Long" }, new Button { Content = "Second" } } };
        var found = Clipped("probe", new Border { Child = clipped }, 120, 0);
        Check.True(found.Count > 0, "a row of buttons wider than its window is found");
        Check.True(found.Any(f => f.Contains("StackPanel")) || found.Any(f => f.Contains("Button")), string.Join(" | ", found));
        var fine = new StackPanel { Children = { new TextBlock { Text = "A long caption that cannot fit in this width", TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = "A long caption that cannot fit in this width", TextTrimming = TextTrimming.CharacterEllipsis } } };
        Check.Equal(Array.Empty<string>(), Clipped("probe", new Border { Child = fine }, 120, 0).ToArray(), "wrapping and trimming text is fine");
    }

    /// <summary>The visible buttons of the toast's action row (the wrap panel that shows), each with its top and height inside the card.</summary>
    private static List<(string Label, double Top, double Height)> RowButtons(ToastWindow toast)
    {
        var root = (FrameworkElement)toast.Content;
        // a label changed after an earlier measure leaves the ancestors' cached measure in place without a running layout manager: measure everything afresh
        foreach (var element in Descendants(root).OfType<UIElement>()) element.InvalidateMeasure();
        root.Measure(new Size(330, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, 330, root.DesiredSize.Height));
        root.UpdateLayout();
        var row = Descendants(root).OfType<WrapPanel>().Single(p => p.Visibility == Visibility.Visible && p.Name.EndsWith("Buttons", StringComparison.Ordinal));
        bool Shows(Button b) { for (DependencyObject? d = b; d != null && d != row; d = VisualTreeHelper.GetParent(d)) if (d is UIElement { Visibility: not Visibility.Visible }) return false; return true; }
        return Descendants(row).OfType<Button>().Where(Shows)
            .Select(b => ((b.Content as string ?? b.Name) + " @" + Math.Round(b.TranslatePoint(new Point(0, 0), root).X) + "+" + Math.Round(b.ActualWidth), Math.Round(b.TranslatePoint(new Point(0, 0), root).Y, 1), Math.Round(b.ActualHeight, 1))).ToList();
    }

    private static string Rows(List<(string Label, double Top, double Height)> buttons) => string.Join(", ", buttons.Select(b => $"{b.Label} top {b.Top} h {b.Height}"));

    private static ToastWindow Split(ToastWindow toast)
    {
        toast.ShareButton.Content = "Share";
        toast.ApplySplitLayout();   // what the constructor does with a token and cloud sharing on
        return toast;
    }

    [UnitTest]
    public static void In_English_the_toast_buttons_stay_in_one_row()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using (L.Scope("en"))
        {
            // plain mode (no token, no cloud sharing: the Share button alone) and split mode (token + cloud sharing: Share and the arrow), the latter also forced on the plain rig
            foreach (var (token, cloud) in new[] { (false, false), (true, true) })
            {
                using var rig = new TrimShareSplitTests.Rig(token, cloud);
                string mode = token ? "split" : "plain";
                var states = new List<(string Name, ToastWindow Toast)>
                {
                    ("clip, " + mode, Toast("clip", L.T("Clip saved"), Toasts.ClipSubtitle(SampleClip()), SampleClip())),
                    ("clip, linked, " + mode, Toast("clip", L.T("Clip saved"), Toasts.ClipSubtitle(SampleClip(true)), SampleClip(true))),
                    ("shared", Toast("shared", L.T("Shared · link copied"), "https://copium.pics/v/abc", SampleClip(true), "https://copium.pics/v/abc")),
                    ("screenshot, Upload", Toast("screenshot", L.T("Screenshot saved"), "2560×1440", null, null, SampleShot(), true)),
                    ("screenshot, Copy link", Toast("screenshot", L.T("Screenshot saved"), "2560×1440", null, null, SampleShot("https://copium.pics/s/abc"), true)),
                };
                if (!token) states.Add(("clip, forced split", Split(Toast("clip", L.T("Clip saved"), Toasts.ClipSubtitle(SampleClip()), SampleClip()))));
                foreach (var (name, toast) in states)
                {
                    var buttons = RowButtons(toast);
                    Check.True(buttons.Count >= 2, name + ": the row has its buttons: " + Rows(buttons));
                    Check.Equal(1, buttons.Select(b => b.Top).Distinct().Count(), name + ": one row in English: " + Rows(buttons));
                    Check.Equal(1, buttons.Select(b => b.Height).Distinct().Count(), name + ": the buttons are equally high: " + Rows(buttons));
                    var root = (FrameworkElement)toast.Content;
                    Check.True(Descendants(root).OfType<WrapPanel>().Where(p => p.Visibility == Visibility.Visible).All(p => p.ActualWidth <= 330 + 0.5), name + ": the row fits the toast");
                }
            }
        }
    }

    [UnitTest]
    public static void In_English_the_busy_smaller_copy_state_wraps_the_toast_buttons_cleanly_into_a_second_row()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var rig = new TrimShareSplitTests.Rig(true, true);   // token + cloud sharing: the split button, the only one that shows a smaller-copy status
        using (L.Scope("en"))
        {
            var toast = Toast("clip", L.T("Clip saved"), Toasts.ClipSubtitle(SampleClip()), SampleClip());
            string quiet = Rows(RowButtons(toast));
            Check.Equal(1, RowButtons(toast).Select(b => b.Top).Distinct().Count(), "before the status: one row: " + quiet);
            // the accepted English change: before this slice the row was a StackPanel and Folder was clipped by the card; the row now wraps
            toast.ShareButton.Content = "Making a smaller copy for Copium... 40%";
            var buttons = RowButtons(toast);
            var tops = buttons.Select(b => b.Top).Distinct().OrderBy(x => x).ToArray();
            Check.Equal(2, tops.Length, "two rows: " + Rows(buttons));
            Check.Equal(1, buttons.Select(b => b.Height).Distinct().Count(), "equally high: " + Rows(buttons));
            Check.True(buttons.Where(b => !(b.Label.StartsWith("Trim", StringComparison.Ordinal) || b.Label.StartsWith("Folder", StringComparison.Ordinal))).All(b => b.Top == tops[0]), "Share and its arrow stay on the first row: " + Rows(buttons));
            Check.True(buttons.Where(b => b.Label.StartsWith("Trim", StringComparison.Ordinal) || b.Label.StartsWith("Folder", StringComparison.Ordinal)).All(b => b.Top == tops[1]), "Trim and Folder share the second row: " + Rows(buttons));
            var root = (FrameworkElement)toast.Content;
            Check.True(Descendants(root).OfType<WrapPanel>().Where(p => p.Visibility == Visibility.Visible).All(p => p.ActualWidth <= 330 + 0.5), "nothing runs off the card");
        }
    }
    [UnitTest]
    public static void A_toast_that_grows_asks_its_owner_to_restack_and_one_that_only_widens_does_not()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var owner = new Toasts(Settings.FromJson("{}"), () => null);
        var toast = new ToastWindow(owner, "clip", SampleClip(), null, "Clip saved", "", TimeSpan.FromSeconds(7), false);
        int before = owner.RelayoutRequests;
        toast.NoteSizeChanged(heightChanged: false);
        Check.Equal(before, owner.RelayoutRequests, "a width change alone: nothing");
        toast.NoteSizeChanged(heightChanged: true);
        Check.Equal(before + 1, owner.RelayoutRequests, "a height change: the stack is laid out again");
        new ToastWindow().NoteSizeChanged(heightChanged: true);   // no owner (v1 constructor): nothing to do, no exception
    }
    /// <summary>The trims-mode delete question laid out at its real width: the row's width, the width its buttons want side by side, the window's width, and where the buttons land.</summary>
    private static (double RowWidth, double Needed, double WindowWidth, List<(string Name, double Left, double Top, double Width)> Buttons) DeleteRow(int trims)
    {
        var w = new ClipDeleteConfirmWindow(ClipDeleter.ConfirmBodyWithTrims("Neon Drift", trims, shared: true), null, trims, null);
        var root = (FrameworkElement)w.Content;
        double width = w.Width;
        root.Measure(new Size(width, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
        root.UpdateLayout();
        var row = Descendants(root).OfType<Panel>().Single(p => p.Children.OfType<Button>().Any(b => b.Name == "CancelButton"));
        var buttons = row.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible).ToList();
        double needed = buttons.Sum(b => b.DesiredSize.Width);   // (a desired size includes the margins)
        var found = buttons.Select(b => { var p = b.TranslatePoint(new Point(0, 0), root); return (b.Name, Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(b.ActualWidth, 1)); }).ToList();
        return (row.ActualWidth, needed, width, found);
    }

    [UnitTest]
    public static void In_English_the_delete_confirm_trims_row_is_one_row_and_right_aligned()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using (L.Scope("en"))
        {
            foreach (int trims in new[] { 2, 12 })
            {
                var (rowWidth, needed, windowWidth, buttons) = DeleteRow(trims);
                string what = $"{trims} trims: window {windowWidth:0}, row {rowWidth:0.0}, buttons want {needed:0.0} (" + string.Join("; ", buttons.Select(b => $"{b.Name} {b.Left}+{b.Width} top {b.Top}")) + ")";
                Check.Equal(3, buttons.Count, "Cancel, Delete full clip only, Delete clip and N trims: " + what);
                Check.Equal(1, buttons.Select(b => b.Top).Distinct().Count(), "one row, as before the row could wrap: " + what);
                Check.True(needed <= windowWidth - 2 - 32, "the buttons fit the window's content width: " + what);
                Check.Near(windowWidth - 17, buttons[^1].Left + buttons[^1].Width, 1.0, "the primary action stays at the right edge: " + what);
                Check.Equal(new[] { "CancelButton", "ClipOnlyButton", "WithTrimsButton" }, buttons.Select(b => b.Name).ToArray(), "reading order: " + what);
            }
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
            foreach (var d in Descendants(VisualTreeHelper.GetChild(root, i))) yield return d;
    }
}
