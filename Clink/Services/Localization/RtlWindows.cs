using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using Clink.UI;
using Clink.UI.Screenshots;

namespace Clink.Services.Localization;

/// <summary>
/// Right-to-left windows for a right-to-left UI language (plan Decision 5: window roots are RTL, a few windows stay LTR).
/// WPF's own <c>Window</c> static constructor already overrides FlowDirection for <c>typeof(Window)</c>, so that type can never be
/// overridden again ("PropertyMetadata is already registered"). Instead every concrete Window subclass of the Clink assembly gets its
/// own override (default FlowDirection and default Style, see <see cref="Override"/>); the element tree below inherits it.
/// </summary>
internal static class RtlWindows
{
    /// <summary>
    /// Windows that stay left-to-right in every language (plan Decision 5): the full-screen region overlay and the status dot are
    /// pixel-positioned over the game/desktop and must not mirror. A subclass of a pinned window is pinned too. This is the ONE place
    /// that list lives; <see cref="TypesToOverride"/> and the tests read it.
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

    /// <summary>
    /// Makes <paramref name="type"/> right-to-left: its FlowDirection default is RightToLeft AND its default Style sets FlowDirection.
    /// The style matters: a metadata default alone is NOT inherited by the child elements (WPF inherits only values that have an
    /// entry, and a default has none; measured in the tests), but a style setter is, so the whole tree mirrors. Returns false when the
    /// type already has its own FlowDirection metadata (it, or its static constructor, overrode it already): never overridden twice.
    /// A window that sets its own Style explicitly replaces the default style (none does today).
    /// </summary>
    public static bool Override(Type type)
    {
        RuntimeHelpers.RunClassConstructor(typeof(Window).TypeHandle);   // Window's own override must exist before ours
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);             // so a static constructor that overrides FlowDirection itself has run (GetMetadata does not run it)
        var baseMeta = FrameworkElement.FlowDirectionProperty.GetMetadata(type.BaseType!);
        if (!ReferenceEquals(FrameworkElement.FlowDirectionProperty.GetMetadata(type), baseMeta)) return false;
        // flags (Inherits, AffectsParentArrange) and callbacks are merged from the base metadata by OverrideMetadata
        FrameworkElement.FlowDirectionProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(FlowDirection.RightToLeft));
        var styleBase = FrameworkElement.StyleProperty.GetMetadata(type.BaseType!);
        if (ReferenceEquals(FrameworkElement.StyleProperty.GetMetadata(type), styleBase) && styleBase.DefaultValue == null)
        {
            var style = new Style(type);
            style.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, FlowDirection.RightToLeft));
            style.Seal();
            FrameworkElement.StyleProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(style));
        }
        return true;
    }
    /// <summary>Overrides every window of the Clink assembly except the pinned ones. Returns how many were overridden.</summary>
    public static int Apply()
    {
        int n = 0;
        foreach (var t in TypesToOverride())
        {
            try { if (Override(t)) n++; }
            catch (Exception ex) { Log.Error("[l10n] could not make " + t.Name + " right-to-left", ex); }
        }
        return n;
    }
}
