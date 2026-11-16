using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// The "What's new" window (checkpoint C3): how it closes, when the start shows it (a game in front holds it back, the 15 s re-check
/// shows it later), that a version counts as seen only once the window is on screen, and the update menu's link. No window is ever put
/// on the screen: the controller's and <see cref="WhatsNewWindow.ShowFor"/>'s show seams take the built window instead.
/// </summary>
internal static class WhatsNewWindowTests
{
    private static ReleaseVersion V(string text) => ReleaseVersion.Parse(text) ?? throw new TestFailure("not a version: " + text);

    private static Changelog Log_()
    {
        Check.True(Changelog.TryParse("## Unreleased\n- U one\n## 0.4.9\n- Four nine\n- Second line\n## 0.4.8\n- Four eight\n", out var log, out string? problem), "parse: " + problem);
        return log!;
    }

    private static Changelog Shipped => WhatsNew.Current ?? throw new TestFailure("the shipped CHANGELOG.md did not load");

    /// <summary>A controller over a scratch settings folder whose "What's new" windows are built and never shown.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Settings Settings;
        public readonly AppController App;
        public readonly List<Window> Shown = new();
        public bool Game;
        private readonly string? _oldIndex = ClipLibrary.IndexPathOverride;

        public Rig()
        {
            if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
            Settings.DirOverride = Dir.Path;
            ClipLibrary.IndexPathOverride = Dir.File("clips.json");
            Settings = Settings.FromJson("{}");
            App = new AppController(Settings, new Updater(Settings))
            {
                GameOrFullScreenProbe = () => Game,
                WhatsNewShow = w => Shown.Add(w),
            };
        }

        public void Dispose()
        {
            try { WhatsNewWindow.Open?.Close(); } catch { }
            App.Dispose();
            ClipLibrary.IndexPathOverride = _oldIndex;
            Dir.Dispose();
        }
    }

    /// <summary>A mouse button goes down (<paramref name="up"/> false) or comes up on <paramref name="on"/>; returns whether the event was handled (so it went nowhere else).</summary>
    private static bool Button(UIElement on, MouseButton button, bool up)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = up ? UIElement.PreviewMouseUpEvent : UIElement.PreviewMouseDownEvent };
        on.RaiseEvent(e);
        return e.Handled;
    }

    /// <summary>A whole click: down, then up.</summary>
    private static void Press(UIElement on, MouseButton button)
    {
        Button(on, button, up: false);
        Button(on, button, up: true);
    }

    private static void Wheel(UIElement on) =>
        on.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });

    /// <summary>A key press on <paramref name="on"/>. KeyEventArgs wants a presentation source, which a window that was never shown has not: a hidden one is made for the event.</summary>
    private static void Key_(UIElement on, Key key)
    {
        using var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("clink-test-keys") { Width = 0, Height = 0, WindowStyle = 0 });
        on.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
    }

    // ------------------------------------------------------------------ closing

    [UnitTest]
    public static void WhatsNewWindow_the_first_click_anywhere_closes_it_after_the_guard_and_the_wheel_does_not()
    {
        long now = 1_000;
        var w = new WhatsNewWindow(Log_().Sections, V("0.4.9"), null) { Clock = () => now };
        try
        {
            Press(w.Body, MouseButton.Left);
            Check.False(w.IsClosed, "a window that was never shown takes no click");
            w.MarkShown();   // what Loaded does
            now += 200;
            Press(w.Body, MouseButton.Left);
            Check.False(w.IsClosed, "inside the 400 ms guard a click does nothing");
            now += 300;
            Wheel(w.Body);
            Wheel(w.Scroller);
            Check.False(w.IsClosed, "the wheel scrolls, it does not close");
            Check.True(Button(w.Body, MouseButton.Left, up: false), "the press is swallowed");
            Check.False(w.IsClosed, "the press alone does not close it: its release would land on the window behind");
            Check.True(Button(w.Body, MouseButton.Left, up: true), "the release is swallowed too");
            Check.True(w.IsClosed, "the first click after the guard closes it, on the release");
        }
        finally { w.Close(); }

        // a release whose press it never saw (or saw inside the guard) does not close it
        long n = 0;
        var stray = new WhatsNewWindow(Log_().Sections, V("0.4.9"), null) { Clock = () => n };
        try
        {
            stray.MarkShown();
            n = 1_000;
            Check.True(Button(stray.Body, MouseButton.Left, up: true), "a stray release is swallowed");
            Check.False(stray.IsClosed, "a release alone does not close it");
            n = 0;
            stray.MarkShown();
            n = 100;
            Button(stray.Body, MouseButton.Left, up: false);   // inside the guard
            n = 1_000;
            Button(stray.Body, MouseButton.Left, up: true);
            Check.False(stray.IsClosed, "a press that began inside the guard does not close it");
            Press(stray.Body, MouseButton.Left);
            Check.True(stray.IsClosed, "the next whole click does");
        }
        finally { stray.Close(); }

        foreach (var button in new[] { MouseButton.Right, MouseButton.Middle, MouseButton.XButton1 })
        {
            long t = 0;
            var other = new WhatsNewWindow(Log_().Sections, V("0.4.9"), null) { Clock = () => t };
            try
            {
                other.MarkShown();
                t = 400;
                Press(other.HintText, button);
                Check.True(other.IsClosed, button + " closes it too, anywhere on it");
            }
            finally { other.Close(); }
        }
    }

    [UnitTest]
    public static void WhatsNewWindow_Esc_Enter_and_Space_close_it_and_the_scroll_keys_do_not()
    {
        Check.Equal(WhatsNewWindow.KeyAction.Close, WhatsNewWindow.ActionFor(Key.Escape, false));
        Check.Equal(WhatsNewWindow.KeyAction.Close, WhatsNewWindow.ActionFor(Key.Enter, false));
        Check.Equal(WhatsNewWindow.KeyAction.Close, WhatsNewWindow.ActionFor(Key.Space, false));
        Check.Equal(WhatsNewWindow.KeyAction.None, WhatsNewWindow.ActionFor(Key.Enter, true), "a held Enter does nothing");
        Check.Equal(WhatsNewWindow.KeyAction.None, WhatsNewWindow.ActionFor(Key.Space, true), "a held Space does nothing");
        Check.Equal(WhatsNewWindow.KeyAction.LineUp, WhatsNewWindow.ActionFor(Key.Up, false));
        Check.Equal(WhatsNewWindow.KeyAction.LineDown, WhatsNewWindow.ActionFor(Key.Down, false));
        Check.Equal(WhatsNewWindow.KeyAction.PageUp, WhatsNewWindow.ActionFor(Key.PageUp, false));
        Check.Equal(WhatsNewWindow.KeyAction.PageDown, WhatsNewWindow.ActionFor(Key.PageDown, false));
        Check.Equal(WhatsNewWindow.KeyAction.Top, WhatsNewWindow.ActionFor(Key.Home, false));
        Check.Equal(WhatsNewWindow.KeyAction.Bottom, WhatsNewWindow.ActionFor(Key.End, false));
        Check.Equal(WhatsNewWindow.KeyAction.None, WhatsNewWindow.ActionFor(Key.A, false));

        long now = 0;
        var w = new WhatsNewWindow(Shipped.Sections, V("0.4.9"), null) { Clock = () => now };
        try
        {
            w.MarkShown();
            now = 1_000;
            foreach (var key in new[] { Key.Down, Key.Up, Key.PageDown, Key.PageUp, Key.End, Key.Home, Key.A })
            {
                Key_(w.Scroller, key);
                Check.False(w.IsClosed, key + " does not close it");
            }
            Key_(w.Scroller, Key.Space);
            Check.True(w.IsClosed, "Space closes it");
        }
        finally { w.Close(); }

        now = 0;
        var early = new WhatsNewWindow(Shipped.Sections, V("0.4.9"), null) { Clock = () => now };
        try
        {
            early.MarkShown();
            now = 100;
            Key_(early.Scroller, Key.Enter);
            Check.False(early.IsClosed, "Enter inside the guard does nothing");
            Key_(early.Scroller, Key.Escape);
            Check.True(early.IsClosed, "Esc always closes it");
        }
        finally { early.Close(); }
    }

    // ------------------------------------------------------------------ the window itself

    [UnitTest]
    public static void WhatsNewWindow_is_capture_excluded_named_for_screen_readers_and_lists_each_section_in_the_language()
    {
        var w = new WhatsNewWindow(Log_().Sections, V("0.5.0-dev.3"), null);
        try
        {
            Check.True(((ICaptureExcludedWindow)w).CaptureExcluded, "kept out of screen captures");
            Check.Equal("WhatsNewWindow", AutomationProperties.GetAutomationId(w));
            Check.Equal("What's new", AutomationProperties.GetName(w));
            Check.Equal("What's new", w.Title);
            Check.Equal("Close", AutomationProperties.GetName(w.CloseButton));
            Check.Equal("Click anywhere or press Esc to close", w.HintText.Text);
            Check.Equal(ScrollBarVisibility.Hidden, w.Scroller.VerticalScrollBarVisibility, "a press on a scroll bar would close it");
            var headings = w.Body.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check.Equal(new[] { "In this dev build (v0.5.0-dev.3)", "Version 0.4.9", "Version 0.4.8" }, headings);
            Check.True(w.Body.Children.OfType<TextBlock>().All(t => AutomationProperties.GetHeadingLevel(t) == AutomationHeadingLevel.Level2), "headings for a screen reader");
            var entries = w.Body.Children.OfType<Grid>().Select(g => ((TextBlock)g.Children[1]).Text).ToArray();
            Check.Equal(new[] { "U one", "Four nine", "Second line", "Four eight" }, entries);
        }
        finally { w.Close(); }

        Check.Equal("Not released yet", WhatsNewWindow.HeadingFor(Log_().Sections[0], V("0.4.9")), "the Unreleased notes on a build that is not a dev build");
        using (L.Scope("ar"))
        {
            Check.Equal(L.T("Version {version}", ("version", "‎‪0.4.9‬‎")), WhatsNewWindow.HeadingFor(Log_().Sections[1], V("0.4.9")), "the version is kept left to right");
            var shipped = Shipped.Released.First();
            var ar = new WhatsNewWindow(new[] { shipped }, V("0.4.9"), null);
            try
            {
                string text = ((TextBlock)ar.Body.Children.OfType<Grid>().First().Children[1]).Text;
                Check.Equal(WhatsNew.Text(shipped.Entries[0]), text);
                Check.True(text != shipped.Entries[0], "the entry is in Arabic: " + text);
            }
            finally { ar.Close(); }
        }
    }

    [UnitTest]
    public static void WhatsNewWindow_ShowFor_keeps_one_window_and_marks_seen_only_once_it_is_on_screen()
    {
        Check.Null(WhatsNewWindow.Open, "none is open to start with");
        int seen = 0, shows = 0;
        try
        {
            var first = WhatsNewWindow.ShowFor(null, Log_().Sections.Skip(1).ToList(), V("0.4.9"), activate: false, () => seen++, _ => shows++);
            Check.Equal(0, seen, "built and handed to Show: not seen yet");
            Check.True(first.ShowInTaskbar, "no visible owner: a taskbar button");
            Check.False(first.ShowActivated, "a start in the tray does not take the focus");
            first.MarkShown();   // Loaded: on screen
            Check.Equal(1, seen, "seen once it is on screen");
            first.MarkShown();
            Check.Equal(1, seen, "once");
            var second = WhatsNewWindow.ShowFor(null, Log_().Sections, V("0.4.9"), activate: true, () => seen++, _ => shows++);
            Check.True(ReferenceEquals(first, second), "the open one is reused");
            Check.Equal(1, shows, "and not shown twice");
            Check.Equal(3, second.Sections.Count, "with the new sections");
            Check.Equal(2, seen, "opening it again while it is on screen is seen at once");
            first.Close();
            Check.Null(WhatsNewWindow.Open, "Close clears the reference");
            Check.Throws<InvalidOperationException>(() => WhatsNewWindow.ShowFor(null, Log_().Sections, V("0.4.9"), true, null, _ => throw new InvalidOperationException("show failed")));
            Check.Null(WhatsNewWindow.Open, "a failed Show leaves nothing registered");
        }
        finally { WhatsNewWindow.Open?.Close(); }
    }

    // ------------------------------------------------------------------ the start

    [UnitTest]
    public static void WhatsNew_a_game_in_front_holds_it_back_and_the_15_s_recheck_shows_it_once_the_screen_is_free()
    {
        using var rig = new Rig();
        var sections = Log_().Sections.Skip(1).ToList();
        Check.Equal(TimeSpan.FromSeconds(15), AppController.WhatsNewRecheck);

        rig.Game = true;
        rig.App.ScheduleWhatsNew(sections, activate: true);   // a start with the window shown: it would have taken the focus at once
        Check.Equal(0, rig.Shown.Count, "not over the game");
        Check.True(rig.App.WhatsNewWaiting, "held back");
        Check.True(rig.App.WhatsNewTimerRunning, "the re-check runs");
        Check.False(rig.App.TryShowPendingWhatsNew(), "the game is still in front");
        Check.Equal(0, rig.Shown.Count);
        Check.True(rig.App.WhatsNewTimerRunning, "and keeps looking");

        rig.Game = false;
        Check.True(rig.App.TryShowPendingWhatsNew(), "the screen is free");
        Check.Equal(1, rig.Shown.Count, "shown once");
        Check.False(rig.App.WhatsNewWaiting);
        Check.False(rig.App.WhatsNewTimerRunning, "the re-check stops");
        var w = (WhatsNewWindow)rig.Shown[0];
        Check.Equal(new[] { "0.4.9", "0.4.8" }, w.Sections.Select(s => s.Heading).ToArray(), "what was decided at start");
        Check.False(w.ShowActivated, "shown after waiting: it never takes the focus from what is in front");
        Check.False(rig.App.TryShowPendingWhatsNew(), "only once");
        Check.Equal(1, rig.Shown.Count);

        // a controller that is gone schedules nothing
        WhatsNewWindow.Open?.Close();
        rig.App.Dispose();
        rig.App.ScheduleWhatsNew(sections, activate: true);
        Check.False(rig.App.WhatsNewWaiting, "nothing after Dispose");
        Check.False(rig.App.WhatsNewTimerRunning);
        Check.Equal(1, rig.Shown.Count);
    }

    [UnitTest]
    public static void WhatsNew_with_no_game_in_front_it_shows_at_once_and_a_test_run_or_dev_instance_decides_nothing()
    {
        using var rig = new Rig();
        rig.App.ScheduleWhatsNew(Log_().Sections.Skip(1).ToList(), activate: true);
        Check.Equal(1, rig.Shown.Count, "shown at once");
        Check.False(rig.App.WhatsNewTimerRunning, "no re-check needed");
        Check.False(rig.App.WhatsNewWaiting);
        WhatsNewWindow.Open?.Close();

        // the start in a unit-test run (as in a --dev instance) shows nothing on its own, first install or not
        rig.App.StartWhatsNew(firstInstall: true, activate: true);
        rig.App.StartWhatsNew(firstInstall: false, activate: true);
        Check.Equal(1, rig.Shown.Count, "nothing more");
        Check.False(rig.App.WhatsNewWaiting);
        Check.Null(rig.Settings.WhatsNewSeen, "and nothing is marked seen");
    }

    [UnitTest]
    public static void WhatsNew_the_version_is_marked_seen_only_when_the_window_is_on_screen()
    {
        using var rig = new Rig();
        rig.App.ScheduleWhatsNew(Shipped.Sections, activate: true);
        Check.Equal(1, rig.Shown.Count, "handed to Show");
        Check.Null(rig.Settings.WhatsNewSeen, "not seen before it is on screen");
        Check.Null(Settings.Load().WhatsNewSeen, "nor saved");
        ((WhatsNewWindow)rig.Shown[0]).MarkShown();   // Loaded
        string current = rig.App.Updater.Current.ToString();
        Check.Equal(current, rig.Settings.WhatsNewSeen, "seen once on screen");
        Check.Equal(current, Settings.Load().WhatsNewSeen, "and saved");
    }

    // ------------------------------------------------------------------ the update menu's link

    [UnitTest]
    public static void WhatsNew_the_update_menu_link_opens_the_whole_changelog_in_front()
    {
        using var rig = new Rig();
        rig.Game = true;
        rig.App.ScheduleWhatsNew(Log_().Sections.Skip(2).ToList(), activate: false);   // an automatic one waiting behind a game
        var main = new MainWindow();
        Check.Equal("What's new", main.WhatsNewLink.Content as string);
        main.WhatsNewLink.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, main.WhatsNewLink));
        Check.Equal(1, rig.Shown.Count, "the link opens it, game or not (the user asked)");
        var w = (WhatsNewWindow)rig.Shown[0];
        Check.Equal(Shipped.Sections.Select(s => s.Heading).ToArray(), w.Sections.Select(s => s.Heading).ToArray(), "every section of the changelog");
        Check.Equal(Shipped.AllEntries.Count(), w.Sections.Sum(s => s.Entries.Count));
        Check.Equal(!QuietMode.Active, w.ShowActivated, "in front");
        Check.False(rig.App.WhatsNewWaiting, "the waiting automatic one is not needed any more");
        Check.False(rig.App.WhatsNewTimerRunning);
        Check.False(main.VersionPopup.IsOpen, "the menu closed");
    }
}
