using System.Reflection;
using System.Windows;
using Clink.UI;
using Clink.UI.Screenshots;

namespace Clink.Services.Localization;

/// <summary>
/// Right-to-left windows for a right-to-left UI language (plan Decision 5: window roots are RTL, a few windows stay LTR).
/// A window is made right-to-left by setting FlowDirection on the root of its CONTENT, not on the Window: FlowDirection on a Window
/// also gives its HWND the WS_EX_LAYOUTRTL style (measured, Tests/LocalizationRtlTests), which would mirror the window frame and the
/// system menu and flip the coordinates of everything that is placed with native calls. Every non-pinned window of the app is chromeless
/// (its own title bar and buttons, drawn in WPF), so mirroring the content mirrors everything the user sees. <see cref="Register"/> does it
/// for every window when it has loaded; <see cref="ApplyTo"/> is the same step for one window (the tests call it).
/// </summary>
internal static class RtlWindows
{
    /// <summary>
    /// Windows that stay left-to-right in every language (plan Decision 5): the full-screen region overlay and the status dot are
    /// pixel-positioned over the game/desktop and must not mirror. A subclass of a pinned window is pinned too. This is the ONE place
    /// that list lives; <see cref="TypesToOverride"/>, <see cref="ApplyTo"/> and the tests read it.
    /// </summary>
    public static readonly IReadOnlyList<Type> PinnedLtr = new[] { typeof(RegionOverlayWindow), typeof(StatusDotWindow) };

    public static bool IsPinned(Type t) => PinnedLtr.Any(p => p.IsAssignableFrom(t));

    /// <summary>Every concrete Window subclass among <paramref name="types"/> that is not pinned LTR, base classes before derived ones.</summary>
    public static IReadOnlyList<Type> TypesToOverride(IEnumerable<Type> types) =>
        types.Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && !IsTestOnly(t) && t != typeof(Window) && typeof(Window).IsAssignableFrom(t) && !IsPinned(t))
             .Distinct()
             .OrderBy(Depth).ThenBy(t => t.FullName, StringComparer.Ordinal)
             .ToList();

    /// <summary>The windows of the Clink assembly that are made right-to-left.</summary>
    public static IReadOnlyList<Type> TypesToOverride() => TypesToOverride(SafeTypes(typeof(RtlWindows).Assembly));

    /// <summary>Windows made up by the tests (namespace Clink.Tests) are not app windows.</summary>
    public static bool IsTestOnly(Type t) => t.Namespace != null && (t.Namespace == "Clink.Tests" || t.Namespace.StartsWith("Clink.Tests.", StringComparison.Ordinal));

    private static int Depth(Type t) { int d = 0; for (var b = t.BaseType; b != null; b = b.BaseType) d++; return d; }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    private static readonly object s_lock = new();
    private static bool s_registered;

    /// <summary>
    /// Makes every window of the app that loads from now on right-to-left (see <see cref="ApplyTo"/>), once per process: one class
    /// handler on Window.Loaded (as <c>QuietMode.RegisterWindowHandlers</c>). Does nothing when the language is not right-to-left.
    /// Returns whether the handler was added by this call.
    /// </summary>
    public static bool Register()
    {
        if (!L.IsRtl) return false;
        lock (s_lock)
        {
            if (s_registered) return false;
            s_registered = true;
        }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        return true;
    }

    /// <summary>The Window.Loaded class handler (internal, for the tests: raising Loaded on a window is a test-guard failure).</summary>
    internal static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w || !ReferenceEquals(e.OriginalSource, w)) return;   // Loaded of a child bubbles up too
        try { ApplyTo(w); }
        catch (Exception ex) { Log.Error("[l10n] could not make " + w.GetType().Name + " right-to-left, it stays left-to-right", ex); }
    }

    /// <summary>
    /// Sets FlowDirection RightToLeft (a local value) on the root of the content of <paramref name="w"/> when the language is
    /// right-to-left and the window is not pinned LTR (<see cref="PinnedLtr"/>). The window itself, so its HWND, stays left-to-right.
    /// Returns whether it changed anything (false: left-to-right language, pinned window, or no element as content).
    /// </summary>
    public static bool ApplyTo(Window w)
    {
        if (!L.IsRtl || IsPinned(w.GetType()) || w.Content is not FrameworkElement root) return false;
        root.FlowDirection = FlowDirection.RightToLeft;
        return true;
    }
}
