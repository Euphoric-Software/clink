using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.UI;

/// <summary>
/// "What's new": the changelog sections it is given (CHANGELOG.md, <see cref="WhatsNew"/>), newest first, every entry in the active
/// language (<see cref="WhatsNew.Text"/>). Shown on its own after an update (the unseen versions, <see cref="WhatsNewPolicy.Decide"/>) and
/// from the update menu's "What's new" link (all of them). Not modal, one at a time, kept out of screen captures, every colour a
/// DynamicResource. The first click of any mouse button anywhere on it closes it (on the release, which it keeps), and so do Esc, Enter and Space; the wheel and the
/// arrow / page keys scroll. Clicks and Enter / Space are ignored for the first <see cref="CopiumConsentWindow.InputDelay"/> (a click
/// meant for the window under it must not dismiss it unread). The version counts as seen only once the window is on screen (Loaded).
/// </summary>
public partial class WhatsNewWindow : Window, ICaptureExcludedWindow
{
    private static WhatsNewWindow? s_open;
    private long _shownAt = long.MaxValue;
    private Action? _onShown;
    private bool _shown;
    private bool _centerOnWorkArea;

    bool ICaptureExcludedWindow.CaptureExcluded => true;

    static WhatsNewWindow() => CaptureExclusion.Register();

    /// <param name="sections">What to show, newest first.</param>
    /// <param name="current">The running version (the heading of the Unreleased notes names it on a dev build).</param>
    /// <param name="onShown">Called once, when the window is on screen (Loaded): where the caller marks the versions seen.</param>
    internal WhatsNewWindow(IReadOnlyList<ChangelogSection> sections, ReleaseVersion current, Action? onShown)
    {
        InitializeComponent();
        _onShown = onShown;
        SetSections(sections, current);
        ShowActivated = !QuietMode.Active;   // a --dev-quiet instance never takes the focus
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height * 0.7);
        SourceInitialized += (_, _) => ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);   // it may come up while recording
        Closed += (_, _) => { IsClosed = true; if (ReferenceEquals(s_open, this)) s_open = null; };
    }

    /// <summary>The open window, or null (a seam for the tests).</summary>
    internal static WhatsNewWindow? Open => s_open;

    /// <summary>The sections on show, newest first.</summary>
    internal IReadOnlyList<ChangelogSection> Sections { get; private set; } = Array.Empty<ChangelogSection>();

    /// <summary>Closed (for the tests: a window that was never shown has no other sign of it).</summary>
    internal bool IsClosed { get; private set; }

    /// <summary>Test seam: milliseconds (Environment.TickCount64 by default) for the input guard.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// Opens the window over <paramref name="owner"/> (centred on it when it is on screen; otherwise on its own with a taskbar button,
    /// centred on the primary work area), or gives the open one the new sections and brings it forward. <paramref name="activate"/> false
    /// (a start in the tray) shows it without taking the focus; a --dev-quiet instance never takes it. <paramref name="show"/> is a seam
    /// for the tests (they must not put a window on screen); the default is <c>Show()</c>. The single-instance reference is set only after
    /// it succeeded.
    /// </summary>
    internal static WhatsNewWindow ShowFor(Window? owner, IReadOnlyList<ChangelogSection> sections, ReleaseVersion current, bool activate, Action? onShown, Action<Window>? show = null)
    {
        if (s_open is { } open)
        {
            open.SetSections(sections, current);
            if (open._shown) onShown?.Invoke();   // already on screen: this opening is seen at once
            else open._onShown = onShown ?? open._onShown;
            if (open.WindowState == WindowState.Minimized)
            {
                if (QuietMode.Active) QuietMode.ShowWithoutActivating(open);
                else open.WindowState = WindowState.Normal;
            }
            if (activate && !QuietMode.Active) open.Activate();
            return open;
        }
        var window = new WhatsNewWindow(sections, current, onShown) { ShowActivated = activate && !QuietMode.Active };
        if (owner is { IsVisible: true, WindowState: not WindowState.Minimized })
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.ShowInTaskbar = true;   // no visible owner: it must not be buried without a taskbar button
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var area = SystemParameters.WorkArea;   // the primary monitor's work area
            window.Left = area.Left + Math.Max(0, (area.Width - window.Width) / 2);
            window.Top = area.Top + area.Height * 0.15;   // centred properly once its height is known (Loaded)
            window._centerOnWorkArea = !QuietMode.Active;   // a quiet instance keeps its windows where the quiet hook puts them
        }
        (show ?? (w => w.Show()))(window);
        s_open = window;
        return window;
    }

    /// <summary>The heading of a section: "Version {version}", or for the Unreleased notes "In this dev build (v{version})" on a dev build and "Not released yet" otherwise.</summary>
    internal static string HeadingFor(ChangelogSection section, ReleaseVersion current)
    {
        if (section.Version is { } v) return L.T("Version {version}", ("version", L.Ltr(v.ToString())));
        return current.IsDevBuild ? L.T("In this dev build (v{version})", ("version", L.Ltr(current.ToString()))) : L.T("Not released yet");
    }

    /// <summary>Fills the body: per section a heading, then one bulleted line per entry in the active language.</summary>
    internal void SetSections(IReadOnlyList<ChangelogSection> sections, ReleaseVersion current)
    {
        Sections = sections;
        Body.Children.Clear();
        bool first = true;
        foreach (var section in sections)
        {
            var heading = new TextBlock
            {
                Text = HeadingFor(section, current),
                Style = (Style)FindResource("Display"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, first ? 2 : 14, 0, 6),
            };
            AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
            Body.Children.Add(heading);
            first = false;
            foreach (string entry in section.Entries)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                // a drawn dot: nothing for a screen reader to read before each sentence, and it sits at the start in either direction
                var bullet = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Margin = new Thickness(3, 7, 9, 0), VerticalAlignment = VerticalAlignment.Top };
                bullet.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Muted");
                var text = new TextBlock { Text = WhatsNew.Text(entry), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(bullet);
                row.Children.Add(text);
                Body.Children.Add(row);
            }
        }
        Scroller.ScrollToTop();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MarkShown();
        if (_centerOnWorkArea)
        {
            var area = SystemParameters.WorkArea;
            Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
        }
        if (ShowActivated) Scroller.Focus();   // the arrow keys scroll from the start
    }

    /// <summary>The window is on screen now: the input guard counts from here, and the caller hears it once (it marks the versions seen).</summary>
    internal void MarkShown()
    {
        _shownAt = Clock();
        if (_shown) return;
        _shown = true;
        try { _onShown?.Invoke(); }
        catch (Exception ex) { Log.Warn("what's new: marking it seen: " + ex.Message); }
    }

    /// <summary>Input is taken once the guard has passed since the window came on screen (a window that was never shown takes none).</summary>
    internal bool InputReady => CopiumConsentWindow.AcceptsInput(_shownAt, Clock());

    /// <summary>What a key does here.</summary>
    internal enum KeyAction { None, Close, LineUp, LineDown, PageUp, PageDown, Top, Bottom }

    /// <summary>Esc, Enter and Space close it (a held Enter / Space does nothing); the arrow, page, Home and End keys scroll; anything else is left alone.</summary>
    internal static KeyAction ActionFor(Key key, bool isRepeat) => key switch
    {
        Key.Escape => KeyAction.Close,
        Key.Enter or Key.Space => isRepeat ? KeyAction.None : KeyAction.Close,
        Key.Up => KeyAction.LineUp,
        Key.Down => KeyAction.LineDown,
        Key.PageUp => KeyAction.PageUp,
        Key.PageDown => KeyAction.PageDown,
        Key.Home => KeyAction.Top,
        Key.End => KeyAction.Bottom,
        _ => KeyAction.None,
    };

    private bool _pressed;   // a button went down on the window after the guard; its release closes it

    /// <summary>
    /// Any button, anywhere on the window (the close glyph included). The press only arms it and takes the mouse capture; the release
    /// closes it. Closing on the press would leave the release to whatever window is behind (a lone mouse-up there can switch the capture
    /// monitor on the monitor map or reopen the version popup). A press inside the guard does not arm it. Both are handled either way.
    /// The wheel is another event and scrolls.
    /// </summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!InputReady) return;
        _pressed = true;
        CaptureMouse();   // the release comes here, wherever the pointer is then (false for a window that is not on screen: the tests)
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // swallowed: with the capture held, nothing behind sees it
        if (!_pressed) return;
        _pressed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (InputReady) Close();
    }

    private void Window_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released && Mouse.MiddleButton == MouseButtonState.Released) return;
        _pressed = false;   // the capture was taken away mid-press (Alt+Tab, another window): that press no longer closes it
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var action = ActionFor(e.Key, e.IsRepeat);
        if (action == KeyAction.None) return;
        e.Handled = true;
        switch (action)
        {
            case KeyAction.Close:
                if (e.Key == Key.Escape || InputReady) Close();
                break;
            case KeyAction.LineUp: Scroller.LineUp(); break;
            case KeyAction.LineDown: Scroller.LineDown(); break;
            case KeyAction.PageUp: Scroller.PageUp(); break;
            case KeyAction.PageDown: Scroller.PageDown(); break;
            case KeyAction.Top: Scroller.ScrollToTop(); break;
            case KeyAction.Bottom: Scroller.ScrollToBottom(); break;
        }
    }

    private static readonly Brush s_fade = MakeFade();

    private static Brush MakeFade()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Colors.Black, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 0.88));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }

    // The bottom fades out while there is more below (the scroll bar is hidden: this is the sign that it scrolls).
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        Scroller.OpacityMask = Scroller.ScrollableHeight > 0 && Scroller.VerticalOffset < Scroller.ScrollableHeight - 1 ? s_fade : null;

    // UI Automation's Invoke on the close button (a mouse press never gets here: the window takes it first).
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
