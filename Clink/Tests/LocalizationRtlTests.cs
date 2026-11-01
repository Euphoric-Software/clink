using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>Right-to-left windows (RtlWindows), message boxes (Dialog) and the compiled-XAML use of {l:T}. Windows are built off screen and never shown.</summary>
internal static class LocalizationRtlTests
{
    [UnitTest]
    public static void The_selection_is_every_app_window_except_the_pinned_ones()
    {
        var all = typeof(RtlWindows).Assembly.GetTypes();
        // independent of the helper: every concrete Window subclass of the app (the tests' own made-up windows are not the app's)
        var appWindows = all.Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(Window).IsAssignableFrom(t) && t != typeof(Window) && !t.Namespace!.StartsWith("Clink.Tests", StringComparison.Ordinal)).ToList();
        Check.True(appWindows.Count >= 13, "reflection finds the app's windows: " + appWindows.Count);
        var selected = RtlWindows.TypesToOverride(all);
        foreach (var w in appWindows)
            Check.True(selected.Contains(w) || RtlWindows.IsPinned(w), w.Name + " is neither mirrored nor pinned LTR: decide it in RtlWindows");
        foreach (var s in selected) Check.True(appWindows.Contains(s) && !RtlWindows.IsPinned(s), s.Name);
        Check.Equal(appWindows.Count - appWindows.Count(RtlWindows.IsPinned), selected.Count, "nothing else is selected or lost");
        foreach (var pinned in RtlWindows.PinnedLtr)
        {
            Check.True(typeof(Window).IsAssignableFrom(pinned), pinned.Name + " is a Window");
            Check.False(selected.Contains(pinned), pinned.Name + " stays LTR");
        }
        var names = RtlWindows.PinnedLtr.Select(t => t.Name).ToArray();
        Check.True(names.Contains("RegionOverlayWindow") && names.Contains("StatusDotWindow"), "the pinned list: " + string.Join(",", names));
        Check.True(selected.Contains(typeof(MainWindow)), "the main window is mirrored; selected: " + string.Join(",", selected.Select(t => t.FullName)));
        Check.Equal(selected.Count, selected.Distinct().Count(), "each type once");
        for (int i = 1; i < selected.Count; i++) Check.True(Depth(selected[i - 1]) <= Depth(selected[i]), "base classes come before derived ones");
    }

    /// <summary>A controller for MainWindow (which reads it in its constructor), on the runner's scratch folder, as the main-window tests do.</summary>
    private static AppController NewApp()
    {
        var settings = Settings.Load();
        return new AppController(settings, new Updater(settings));
    }

    private static int Depth(Type t) { int d = 0; for (var b = t.BaseType; b != null; b = b.BaseType) d++; return d; }

    /// <summary>Whether the HWND of <paramref name="w"/> (created here, never shown) has WS_EX_LAYOUTRTL.</summary>
    private static bool HasLayoutRtl(Window w)
    {
        nint hwnd = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        nint ex = Windows.Win32.PInvoke.GetWindowLongPtr(new Windows.Win32.Foundation.HWND(hwnd), Windows.Win32.UI.WindowsAndMessaging.WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        return (ex & 0x00400000) != 0;
    }

    private static Window Probe(bool windowRtl, bool transparent)
    {
        var w = new Window { ShowInTaskbar = false, WindowStyle = WindowStyle.None, Content = new Grid { Children = { new TextBlock { Text = "x" } } } };
        if (transparent) w.AllowsTransparency = true;
        if (windowRtl) w.FlowDirection = FlowDirection.RightToLeft;
        return w;
    }

    [UnitTest]
    public static void FlowDirection_on_the_window_itself_mirrors_its_HWND_so_only_the_content_root_is_set()
    {
        // measured: WPF gives the HWND of a right-to-left Window the WS_EX_LAYOUTRTL style, whether or not it is transparent
        foreach (bool transparent in new[] { false, true })
        {
            var w = Probe(windowRtl: true, transparent);
            try { Check.True(HasLayoutRtl(w), "Window.FlowDirection RightToLeft sets WS_EX_LAYOUTRTL (transparent " + transparent + ")"); }
            finally { w.Close(); }
        }
        // the mechanism: the content root is right-to-left and the HWND is not
        foreach (bool transparent in new[] { false, true })
            using (L.Scope("ar"))
            {
                var w = Probe(windowRtl: false, transparent);
                try
                {
                    Check.True(RtlWindows.ApplyTo(w));
                    Check.Equal(FlowDirection.RightToLeft, ((FrameworkElement)w.Content).FlowDirection);
                    Check.Equal(FlowDirection.LeftToRight, w.FlowDirection, "the window stays left-to-right");
                    Check.False(HasLayoutRtl(w), "so does its HWND (transparent " + transparent + ")");
                }
                finally { w.Close(); }
            }
    }

    [UnitTest]
    public static void ApplyTo_mirrors_the_content_root_of_app_windows_in_a_right_to_left_language()
    {
        using var app = NewApp();
        var built = new List<Window>();
        try
        {
            built.Add(new ToastWindow());
            built.Add(new ThirdPartyNoticesWindow());
            built.Add(new ClipDeleteConfirmWindow("Body", null));
            built.Add(new MainWindow());
            foreach (string code in new[] { "ar", L.PseudoRtlCode })
                using (L.Scope(code))
                    foreach (var w in built)
                    {
                        var root = (FrameworkElement)w.Content;
                        root.ClearValue(FrameworkElement.FlowDirectionProperty);
                        Check.Equal(FlowDirection.LeftToRight, root.FlowDirection, w.GetType().Name + " starts left-to-right");
                        Check.True(RtlWindows.ApplyTo(w), code + " " + w.GetType().Name);
                        Check.Equal(FlowDirection.RightToLeft, root.FlowDirection, code + " " + w.GetType().Name + ": the content root");
                        Check.Equal(FlowDirection.LeftToRight, w.FlowDirection, w.GetType().Name + ": the window itself");
                        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(root) > 0)
                            Check.Equal(FlowDirection.RightToLeft, ((FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(root, 0)).FlowDirection, w.GetType().Name + ": and below it");
                    }
            foreach (string code in new[] { "en", "qps-ploc" })
                using (L.Scope(code))
                    foreach (var w in built)
                    {
                        ((FrameworkElement)w.Content).ClearValue(FrameworkElement.FlowDirectionProperty);
                        Check.False(RtlWindows.ApplyTo(w), code + " " + w.GetType().Name + ": a left-to-right language changes nothing");
                        Check.Equal(FlowDirection.LeftToRight, ((FrameworkElement)w.Content).FlowDirection);
                    }
        }
        finally { foreach (var w in built) { try { w.Close(); } catch { } } }
    }

    [UnitTest]
    public static void ApplyTo_skips_pinned_windows_and_windows_without_an_element_as_content()
    {
        using (L.Scope("ar"))
        {
            var dot = new StatusDotWindow();
            try
            {
                Check.True(RtlWindows.IsPinned(dot.GetType()));
                Check.False(RtlWindows.ApplyTo(dot), "the status dot is pinned");
                if (dot.Content is FrameworkElement root) Check.Equal(FlowDirection.LeftToRight, root.FlowDirection);
            }
            finally { dot.Close(); }
            Check.True(RtlWindows.IsPinned(typeof(Clink.UI.Screenshots.RegionOverlayWindow)), "and so is the region overlay");
            var bare = new Window { Content = "text" };
            Check.False(RtlWindows.ApplyTo(bare), "a string as content is not an element");
            Check.False(RtlWindows.ApplyTo(new Window()), "no content");
        }
    }

    [UnitTest]
    public static void Register_hooks_Window_Loaded_only_in_a_right_to_left_language()
    {
        using (L.Scope("en")) Check.False(RtlWindows.Register(), "nothing to register for a left-to-right language");
        using (L.Scope("ar"))
        {
            bool first = RtlWindows.Register();   // false when an earlier test of this run did it: once per process
            Check.False(RtlWindows.Register(), "a second call adds nothing" + (first ? "" : " (it was added earlier in this run)"));

            var w = new Window { Content = new Grid() };
            RtlWindows.OnWindowLoaded(w, new RoutedEventArgs(FrameworkElement.LoadedEvent, w));   // what the class handler runs (raising Loaded would trip the test guard)
            Check.Equal(FlowDirection.RightToLeft, ((FrameworkElement)w.Content).FlowDirection, "a window that loads is mirrored");
            Check.Equal(FlowDirection.LeftToRight, w.FlowDirection);

            var child = new Grid();
            var w2 = new Window { Content = child };
            RtlWindows.OnWindowLoaded(w2, new RoutedEventArgs(FrameworkElement.LoadedEvent, new Button()));   // defensive: only the window's own Loaded counts
            Check.Equal(FlowDirection.LeftToRight, child.FlowDirection, "a child's Loaded is ignored");

            var dot = new StatusDotWindow();
            try
            {
                RtlWindows.OnWindowLoaded(dot, new RoutedEventArgs(FrameworkElement.LoadedEvent, dot));
                if (dot.Content is FrameworkElement root) Check.Equal(FlowDirection.LeftToRight, root.FlowDirection, "the pinned status dot stays left-to-right");
            }
            finally { dot.Close(); }
        }
        // the handler stays for the rest of the run, but does nothing once the language is left-to-right again
        var late = new Window { Content = new Grid() };
        RtlWindows.OnWindowLoaded(late, new RoutedEventArgs(FrameworkElement.LoadedEvent, late));
        Check.Equal(FlowDirection.LeftToRight, ((FrameworkElement)late.Content).FlowDirection, "English: nothing happens");
    }

    [UnitTest]
    public static void Every_app_window_has_an_element_as_its_content_root()
    {
        using var app = NewApp();
        // built off screen (never shown): the windows that can be made without a controller, an entry or a request
        var built = new List<Window>();
        try
        {
            built.Add(new ToastWindow());
            built.Add(new ThirdPartyNoticesWindow());
            built.Add(new ClipDeleteConfirmWindow("Body", null));
            built.Add(new MainWindow());
            built.Add(new StatusDotWindow());
            foreach (var w in built) Check.True(w.Content is FrameworkElement, w.GetType().Name + ": content " + (w.Content?.GetType().Name ?? "null"));
        }
        finally { foreach (var w in built) { try { w.Close(); } catch { } } }

        // the others need arguments: read their XAML. The root has exactly one child that is not a property element, and it is an element
        // (a Grid, Border ...), so Window.Content is a FrameworkElement.
        string? root = L10nSourceScan.Root();
        if (root == null) return;
        var done = built.Select(w => w.GetType().Name).ToHashSet();
        var checkedByXaml = new List<string>();
        foreach (var t in RtlWindows.TypesToOverride().Concat(RtlWindows.PinnedLtr))
        {
            if (done.Contains(t.Name)) continue;
            string? path = new[] { "", "UI", @"UI\Screenshots" }.Select(d => System.IO.Path.Combine(root, d, t.Name + ".xaml")).FirstOrDefault(System.IO.File.Exists);
            Check.NotNull(path, t.Name + " has a XAML file next to its code");
            var children = XDocument.Load(path!).Root!.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
            Check.Equal(1, children.Count, t.Name + ": one content element under <Window>");
            Check.Equal("http://schemas.microsoft.com/winfx/2006/xaml/presentation", children[0].Name.NamespaceName, t.Name + ": " + children[0].Name.LocalName + " is a WPF element");
            checkedByXaml.Add(t.Name);
        }
        Check.True(checkedByXaml.Count >= 5, "the XAML check ran for: " + string.Join(", ", checkedByXaml));
    }

    [UnitTest]
    public static void Message_boxes_read_right_to_left_only_in_a_right_to_left_language()
    {
        using (L.Scope("en")) Check.Equal(MessageBoxOptions.None, Dialog.Options);
        using (L.Scope("qps-ploc")) Check.Equal(MessageBoxOptions.None, Dialog.Options);
        foreach (string code in new[] { "ar", L.PseudoRtlCode })
            using (L.Scope(code)) Check.Equal(MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign, Dialog.Options, code);
        using (L.Scope("ar")) Check.Equal("None", L.InEnglish(() => Dialog.Options.ToString()), "a log-time composition sees English");
    }

    [UnitTest]
    public static void Every_message_box_goes_through_Dialog()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // not run from a checkout (L10nSourceScan.Root throws on CI)
        string needle = "MessageBox" + ".Show(";
        var offenders = new List<string>();
        int seen = 0;
        foreach (string file in System.IO.Directory.EnumerateFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            string rel = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("Tests/", StringComparison.Ordinal)) continue;
            seen++;
            if (rel == "UI/Dialog.cs") continue;
            foreach (string line in System.IO.File.ReadLines(file))
                if (line.Contains(needle, StringComparison.Ordinal) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal)) offenders.Add(rel + ": " + line.Trim());
        }
        Check.True(seen > 100, "the scan read the sources: " + seen);
        Check.Equal(Array.Empty<string>(), offenders.ToArray(), "MessageBox calls outside UI/Dialog.cs (use Dialog.Show: it adds the right-to-left options)");
        Check.True(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "UI", "Dialog.cs")).Contains(needle, StringComparison.Ordinal), "Dialog itself calls it");
    }

    [UnitTest]
    public static void Dev_language_is_a_dev_only_switch_that_names_a_language_and_survives_a_restart()
    {
        using var dir = new TempDir();
        Check.Null(DevOptions.Parse(new[] { "--dev", dir.Path })!.Language, "off unless given");
        foreach (var (given, canonical) in new[] { ("qps-plocm", "qps-plocm"), ("QPS-PLOC", "qps-ploc"), ("ar", "ar"), ("AR", "ar"), ("pt_br", "pt-BR"), ("zh-cn", "zh-CN") })
            Check.Equal(canonical, DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-language", given })!.Language, given);
        Check.Equal("ar", DevOptions.Parse(new[] { "--dev-language", "ar", "--dev", dir.Path })!.Language, "the order does not matter");
        Check.Equal("ar", DevOptions.Parse(new[] { "--dev", dir.Path, "--DEV-LANGUAGE", "\"ar\"" })!.Language, "case and quotes do not matter");

        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev-language", "ar" }), "without --dev it is an error, never a normal start");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-language" }), "a code is needed");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-language", "--dev-record" }), "a switch is not a code");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-language", "klingon" }), "an unknown language");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-languages", "ar" }), "a look-alike");

        Check.Equal(new[] { "--dev", @"C:\d", "--dev-language", "qps-plocm" }, DevOptions.Switches(@"C:\d", false, false, false, null, language: "qps-plocm").ToArray(), "a restart keeps it");
        Check.Equal(new[] { "--dev", @"C:\d" }, DevOptions.Switches(@"C:\d", false, false, false, null).ToArray());
        Check.Equal(new[] { "--show" }, DevOptions.WithoutSwitches(new[] { "--dev", @"C:\d", "--dev-language", "ar", "--show" }).ToArray(), "and the helper drops it with its value");

        string before = L.Code;
        using (L.Scope("en"))
        {
            Check.Equal("qps-plocm", L.SetForDev("qps-plocm"));
            Check.True(L.IsRtl, "right-to-left");
            Check.True(PseudoLocale.IsPseudo(L.T("Cancel")), "pseudo text");
            Check.Equal("ar", L.SetForDev("AR"));
            Check.True(L.IsRtl);
            Check.Equal("en", L.SetForDev("zz"), "anything else is English");
            Check.False(L.IsRtl);
        }
        Check.Equal(before, L.Code, "the scope put the language back");
    }

    [UnitTest]
    public static void The_markup_extension_works_in_compiled_xaml_through_a_clr_namespace()
    {
        var set = CatalogSet.Build(new[] { new CatalogSource("en", "t", """{ "Save clip": "Save clip", "Don't save": "Don't save", "Cancel": "Cancel" }""") });
        using var _ = L.UseCatalogs(set);
        using (L.Scope("en"))
        {
            var c = new L10nCompiledProbe();
            var panel = (StackPanel)c.Content;
            Check.Equal("Save clip", ((TextBlock)panel.Children[0]).Text);
            Check.Equal("Don't save", (string)((TextBlock)panel.Children[0]).ToolTip);
            Check.Equal("Cancel", (string)((Button)panel.Children[1]).Content);
            Check.Equal("Cancel", System.Windows.Automation.AutomationProperties.GetName(panel.Children[1]));
        }
        using (L.Scope("qps-ploc"))
        {
            var c = new L10nCompiledProbe();
            Check.Equal(Array.Empty<string>(), L10nTreeHarness.Unlocalized(c).ToArray(), "compiled XAML goes through L");
            Check.True(PseudoLocale.IsPseudo(((TextBlock)((StackPanel)c.Content).Children[0]).Text));
        }
    }
}
