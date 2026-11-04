using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clink.Screenshots.Editing;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>
/// Slice S6 phase B, the walker: under the pseudo right-to-left language (qps-plocm) every app window is made right-to-left the way the
/// Loaded handler does it (RtlWindows.ApplyTo; no window is shown), and then its whole tree is checked: the surfaces that must stay
/// left to right (pictures, the timeline, the replay-length slider, the editor canvas, the monitor map) are pinned as local values,
/// the content roots are right-to-left, and no context menu is left reading left to right.
/// </summary>
internal static class LocalizationRtlWalkerTests
{
    private static bool IsPinnedKind(DependencyObject d) =>
        d is Image or TimelineControl or ReplayLengthSlider or EditorCanvas or MonitorMap;

    /// <summary>The element and everything under it: logical children, visual children (templates applied), data templates in resources, tooltips and menus.</summary>
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            yield return node;
            if (node is FrameworkElement fe)
            {
                try { fe.ApplyTemplate(); } catch { }
                foreach (var template in Templates(fe.Resources))
                {
                    DependencyObject? content = null;
                    try { content = template.LoadContent(); } catch { }
                    if (content != null) stack.Push(content);
                }
                if (fe.ToolTip is DependencyObject tip) stack.Push(tip);
                if (fe.ContextMenu is { } menu) stack.Push(menu);
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject d) stack.Push(d);
            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
                for (int i = 0, n = VisualTreeHelper.GetChildrenCount(node); i < n; i++) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static IEnumerable<DataTemplate> Templates(ResourceDictionary resources)
    {
        foreach (object? value in resources.Values) if (value is DataTemplate dt) yield return dt;
        foreach (var merged in resources.MergedDictionaries)
            foreach (var dt in Templates(merged)) yield return dt;
    }

    /// <summary>Whether FlowDirection was set on the element itself (a local value, or by its template or style), not just inherited.</summary>
    private static bool SetOnTheElement(DependencyObject d)
    {
        var source = DependencyPropertyHelper.GetValueSource(d, FrameworkElement.FlowDirectionProperty).BaseValueSource;
        return source is BaseValueSource.Local or BaseValueSource.ParentTemplate or BaseValueSource.ParentTemplateTrigger or BaseValueSource.Style or BaseValueSource.StyleTrigger or BaseValueSource.TemplateTrigger;
    }

    /// <summary>The findings for one window made right-to-left: what is wrong, one line each.</summary>
    private static List<string> Check_(string name, Window window)
    {
        var bad = new List<string>();
        bool applied = RtlWindows.ApplyTo(window);
        if (!applied) { bad.Add(name + ": ApplyTo did nothing"); return bad; }
        if (window.Content is not FrameworkElement root || root.FlowDirection != FlowDirection.RightToLeft) bad.Add(name + ": the content root is not right-to-left");
        if (window.FlowDirection != FlowDirection.LeftToRight) bad.Add(name + ": the window itself is not left to right (its HWND would mirror)");
        int pinned = 0;
        foreach (var node in Tree(window))
        {
            if (IsPinnedKind(node))
            {
                pinned++;
                var fe = (FrameworkElement)node;
                if (!SetOnTheElement(fe)) bad.Add(name + ": " + node.GetType().Name + (fe.Name.Length > 0 ? "#" + fe.Name : "") + " inherits its direction instead of pinning it left to right");
                else if (fe.FlowDirection != FlowDirection.LeftToRight) bad.Add(name + ": " + node.GetType().Name + (fe.Name.Length > 0 ? "#" + fe.Name : "") + " is not left to right");
            }
            if (node is FrameworkElement { ContextMenu: { } menu } owner && menu.FlowDirection != FlowDirection.RightToLeft)
                bad.Add(name + ": the context menu of " + owner.GetType().Name + " reads left to right");
        }
        return bad;
    }

    private static AppController NewApp()
    {
        var settings = Settings.Load();   // the runner's scratch folder
        settings.CaptureBorderNoticeDismissed = true;
        return new AppController(settings, new Updater(settings));
    }

    [UnitTest]
    public static void Under_the_pseudo_right_to_left_language_every_window_is_mirrored_and_the_pinned_surfaces_stay_left_to_right()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var found = new List<string>();
        int windows = 0, pinnedSeen = 0;
        var kinds = new HashSet<string>();
        void One(string name, Window w)
        {
            windows++;
            found.AddRange(Check_(name, w));
            foreach (var n in Tree(w)) if (IsPinnedKind(n)) { pinnedSeen++; kinds.Add(n.GetType().Name); }
        }

        using (var app = NewApp())
        using (L.Scope(L.PseudoRtlCode))
        {
            var main = new MainWindow();
            typeof(MainWindow).GetMethod("Populate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
            One("MainWindow", main);
            One("ArchiveWindow", new ArchiveWindow(app));
            One("ThirdPartyNoticesWindow", new ThirdPartyNoticesWindow());
            One("ClipDeleteConfirmWindow", new ClipDeleteConfirmWindow(ClipDeleter.ConfirmBodyWithTrims("Neon Drift", 2, shared: true), null, 2, null));
            One("CopiumConsentWindow", new CopiumConsentWindow(CopiumConsentWindow.Mode.Consent, offerAutoUpload: true, excludeFromCapture: false));
            One("CopiumWelcomeWindow", new CopiumWelcomeWindow(new CopiumWelcomeModel(true, (_, _) => { }), "Jason", () => false));
            var clip = new ClipEntry { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-l10n-nonexistent", "Neon Drift 2026-10-20 21-04-11.mp4"), Game = "Neon Drift", Size = 190_000_000, Duration = 90, Created = DateTime.Now };
            One("ShareWindow", new ShareWindow(clip));
            One("ShareFilesWindow", new ShareFilesWindow(new[] { new ShareFileItem(@"\\nas\clips\x.mp4", true, 1000, 1) }, false));
            One("ToastWindow", new ToastWindow(null!, "info", clip, null, "Clip saved", "Neon Drift · 1:30 · 190 MB", TimeSpan.FromSeconds(7), false, null, false));
            One("InsertImagePicker", new InsertImagePicker(new InsertPickerModel(null), canPaste: true));
            One("EditorToolbar", new Window { Content = new EditorToolbar() });
            One("EditorCanvas", new Window { Content = new StackPanel { Children = { new EditorCanvas() } } });
        }
        using (var rig = new TrimShareSplitTests.Rig(true, true))
        using (L.Scope(L.PseudoRtlCode))
        using (var win = new TrimShareSplitTests.Win(rig.Entry))
            One("TrimWindow", win.Window);

        Check.Equal(Array.Empty<string>(), found.ToArray(), "right-to-left findings");
        Check.True(windows >= 13, "windows walked: " + windows);
        foreach (string kind in new[] { "Image", "TimelineControl", "ReplayLengthSlider", "EditorCanvas", "MonitorMap" })
            Check.True(kinds.Contains(kind), "the walk met a " + kind + " (met: " + string.Join(", ", kinds) + ")");
        Check.True(pinnedSeen >= 8, "pinned surfaces walked: " + pinnedSeen);
    }

    [UnitTest]
    public static void Path_boxes_stay_left_to_right_and_translated_sentences_follow_the_direction_of_the_window()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var app = NewApp();
        using (L.Scope(L.PseudoRtlCode))
        {
            var main = new MainWindow();
            typeof(MainWindow).GetMethod("Populate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
            Check.True(RtlWindows.ApplyTo(main));
            // Windows keeps a path edit box left to right (the custom-app box holds an exe name). These are all the text boxes of the app whose content is a file system path or an exe name; the other text boxes (the pairing code, the device name, the numbers) are pinned or are not paths
            foreach (string name in new[] { "ClipsFolderBox", "ArchivePathBox", "ScreenshotFolderBox", "CustomAppBox" })
            {
                var box = (TextBox)main.FindName(name);
                Check.True(SetOnTheElement(box) && box.FlowDirection == FlowDirection.LeftToRight, name + " is pinned left to right");
            }
            var settingsDir = (TextBlock)main.FindName("SettingsDirText");
            Check.False(SetOnTheElement(settingsDir), "\"Saved in {folder}\" is a translated sentence: not pinned");
            Check.Equal(FlowDirection.RightToLeft, settingsDir.FlowDirection, "it reads in the direction of the window");

            // the archive window's destination line: a bare path is left to right, the sentence follows the window
            var none = new ArchiveWindow(app);
            Check.True(RtlWindows.ApplyTo(none));
            var noneText = (TextBlock)none.FindName("DestinationText");
            Check.False(SetOnTheElement(noneText), "\"No archive location is set\": not pinned");
            Check.Equal(FlowDirection.RightToLeft, noneText.FlowDirection);
            string? before = app.Settings.ArchivePath;
            app.Settings.ArchivePath = @"D:\Clips";
            try
            {
                var path = new ArchiveWindow(app);
                Check.True(RtlWindows.ApplyTo(path));
                var pathText = (TextBlock)path.FindName("DestinationText");
                Check.True(SetOnTheElement(pathText) && pathText.FlowDirection == FlowDirection.LeftToRight, "a bare path is pinned left to right");
            }
            finally { app.Settings.ArchivePath = before; }
        }
    }
    [UnitTest]
    public static void The_regional_windows_stay_pinned_and_a_left_to_right_language_changes_nothing()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using (L.Scope(L.PseudoRtlCode))
        {
            var dot = new StatusDotWindow();
            Check.False(RtlWindows.ApplyTo(dot), "the status dot stays pinned");
        }
        using (L.Scope("en"))
        {
            var w = new ThirdPartyNoticesWindow();
            Check.False(RtlWindows.ApplyTo(w), "English: nothing to do");
            Check.Equal(FlowDirection.LeftToRight, ((FrameworkElement)w.Content).FlowDirection);
            var toolbar = new EditorToolbar();
            Check.True(toolbar.UndoButton.RenderTransform.Value.IsIdentity, "English: no mirrored glyph");
        }
    }
}
