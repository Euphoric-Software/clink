using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>Right-to-left windows (RtlWindows) and the compiled-XAML use of {l:T}. No test overrides anything for a real app window or typeof(Window).</summary>
internal static class LocalizationRtlTests
{
    // test-only windows (namespace Clink.Tests: RtlWindows leaves them out of the app's selection)
    private sealed class ProbeWindow : Window { }
    private sealed class ProbeParent : Window { }
    private sealed class ProbeChild : Window { }   // overridden on its own, base is Window
    private sealed class OwnStaticWindow : Window
    {
        static OwnStaticWindow() => FrameworkElement.FlowDirectionProperty.OverrideMetadata(typeof(OwnStaticWindow), new FrameworkPropertyMetadata(FlowDirection.LeftToRight));
    }

    [UnitTest]
    public static void Window_already_overrides_FlowDirection_so_typeof_Window_cannot_be_overridden()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Window).TypeHandle);
        var window = FrameworkElement.FlowDirectionProperty.GetMetadata(typeof(Window));
        var element = FrameworkElement.FlowDirectionProperty.GetMetadata(typeof(FrameworkElement));
        Check.False(ReferenceEquals(window, element), "Window has its own FlowDirection metadata after its static constructor");
        Check.Throws<ArgumentException>(() => FrameworkElement.FlowDirectionProperty.OverrideMetadata(typeof(Window), new FrameworkPropertyMetadata(FlowDirection.RightToLeft)));
        Check.Equal(FlowDirection.LeftToRight, new Window().FlowDirection, "a failed attempt changed nothing");
    }

    [UnitTest]
    public static void The_selection_is_every_app_window_except_the_pinned_ones()
    {
        var all = typeof(RtlWindows).Assembly.GetTypes();
        // independent of the helper: every concrete Window subclass of the app (the tests' own made-up windows are not the app's)
        var appWindows = all.Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && typeof(Window).IsAssignableFrom(t) && t != typeof(Window) && !t.Namespace!.StartsWith("Clink.Tests", StringComparison.Ordinal)).ToList();
        Check.True(appWindows.Count >= 13, "reflection finds the app's windows: " + appWindows.Count);
        var selected = RtlWindows.TypesToOverride(all);
        foreach (var w in appWindows)
            Check.True(selected.Contains(w) || RtlWindows.IsPinned(w), w.Name + " is neither overridden nor pinned LTR: decide it in RtlWindows");
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

    private static int Depth(Type t) { int d = 0; for (var b = t.BaseType; b != null; b = b.BaseType) d++; return d; }

    [UnitTest]
    public static void Overriding_a_window_subclass_makes_new_instances_and_their_children_right_to_left_only_for_it()
    {
        Check.True(RtlWindows.Override(typeof(ProbeWindow)) || IsRtl(typeof(ProbeWindow)), "overridden (or already, in a repeated run)");
        Check.False(RtlWindows.Override(typeof(ProbeWindow)), "a second call does nothing");
        var w = new ProbeWindow();
        Check.Equal(FlowDirection.RightToLeft, w.FlowDirection);
        var child = new TextBlock();
        w.Content = child;
        Check.Equal(FlowDirection.RightToLeft, child.FlowDirection, "the element tree inherits it (a bare metadata default would not reach the children)");
        Check.Equal(FlowDirection.LeftToRight, new Window().FlowDirection, "a plain Window stays left-to-right");
        Check.Equal(FlowDirection.LeftToRight, new ProbeParent().FlowDirection, "so do windows not overridden");
    }

    private static bool IsRtl(Type t) => (FlowDirection)FrameworkElement.FlowDirectionProperty.GetMetadata(t).DefaultValue == FlowDirection.RightToLeft;

    [UnitTest]
    public static void A_window_whose_own_static_constructor_overrides_FlowDirection_is_left_alone()
    {
        Check.False(RtlWindows.Override(typeof(OwnStaticWindow)), "no second override");
        Check.Equal(FlowDirection.LeftToRight, new OwnStaticWindow().FlowDirection);
    }

    [UnitTest]
    public static void A_window_that_is_overridden_on_its_own_works()
    {
        RtlWindows.Override(typeof(ProbeChild));
        Check.Equal(FlowDirection.RightToLeft, new ProbeChild().FlowDirection);
        Check.False(RtlWindows.Override(typeof(ProbeChild)));
    }

    // two independent base -> derived pairs, one overridden base-first and one derived-first (each pair can be overridden once per process)
    private class BaseFirst : Window { }
    private sealed class DerivedOfBaseFirst : BaseFirst { }
    private class DerivedFirstBase : Window { }
    private sealed class DerivedFirstDerived : DerivedFirstBase { }

    [UnitTest]
    public static void A_derived_window_is_right_to_left_whichever_order_base_and_derived_are_overridden_in()
    {
        // base first (what TypesToOverride does: base classes before derived ones)
        bool baseDone = RtlWindows.Override(typeof(BaseFirst));
        bool derivedDone = RtlWindows.Override(typeof(DerivedOfBaseFirst));
        Check.True((baseDone && derivedDone) || (IsRtl(typeof(BaseFirst)) && IsRtl(typeof(DerivedOfBaseFirst))), "base then derived");
        Check.False(RtlWindows.Override(typeof(BaseFirst)) || RtlWindows.Override(typeof(DerivedOfBaseFirst)), "nothing is overridden twice");
        var d1 = new DerivedOfBaseFirst();
        var c1 = new TextBlock();
        d1.Content = c1;
        Check.Equal(FlowDirection.RightToLeft, d1.FlowDirection, "derived instance");
        Check.Equal(FlowDirection.RightToLeft, c1.FlowDirection, "its child");
        var baseStyle = new BaseFirst().Style;
        Check.NotNull(baseStyle, "the base has the RTL default style");
        Check.True(ReferenceEquals(baseStyle, d1.Style), "the derived window reuses the base's style (it does not stack a second one)");

        // derived first, then its base
        bool derived2 = RtlWindows.Override(typeof(DerivedFirstDerived));
        bool base2 = RtlWindows.Override(typeof(DerivedFirstBase));
        Check.True((derived2 && base2) || (IsRtl(typeof(DerivedFirstDerived)) && IsRtl(typeof(DerivedFirstBase))), "derived then base");
        var d2 = new DerivedFirstDerived();
        var c2 = new TextBlock();
        d2.Content = c2;
        Check.Equal(FlowDirection.RightToLeft, d2.FlowDirection, "derived instance (derived overridden first)");
        Check.Equal(FlowDirection.RightToLeft, c2.FlowDirection, "its child");
        var b2 = new DerivedFirstBase();
        Check.Equal(FlowDirection.RightToLeft, b2.FlowDirection, "the base too");
        Check.NotNull(d2.Style);
        Check.NotNull(b2.Style);
        Check.Equal(FlowDirection.LeftToRight, new Window().FlowDirection, "a plain Window stays left-to-right");
    }

    [UnitTest]
    public static void No_app_window_sets_its_own_Style_and_no_resource_is_keyed_by_a_Window_type()
    {
        // 1. no implicit style for a Window type in the application resources (it would replace RtlWindows' default style)
        var app = Application.Current ?? throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        var offenders = new List<string>();
        void Scan(ResourceDictionary d, string where)
        {
            foreach (object key in d.Keys)
                if (key is Type t && typeof(Window).IsAssignableFrom(t)) offenders.Add(where + ": " + t.Name);
            foreach (var m in d.MergedDictionaries) Scan(m, where + "/merged");
        }
        Scan(app.Resources, "App.Resources");
        Check.Equal(Array.Empty<string>(), offenders.ToArray(), "implicit styles keyed by a Window type");

        // 2. the windows that can be built without arguments, built off screen and never shown: none may have an explicit Style (an
        //    explicit Style replaces RtlWindows' default style, so the window would silently stay left-to-right) nor a Window-typed
        //    implicit style in its own resources. The windows that need a controller, an entry or a request cannot be built in the
        //    test host; for those the rule is the doc comment on RtlWindows.Override plus the review checklist.
        var priorMain = app.MainWindow;
        var built = new List<Window>();
        try
        {
            built.Add(new ToastWindow());
            built.Add(new ThirdPartyNoticesWindow());
            built.Add(new ClipDeleteConfirmWindow("Body", null));
            foreach (var w in built)
            {
                Check.True(w.Style == null || ReferenceEquals(w.Style, FrameworkElement.StyleProperty.GetMetadata(w.GetType()).DefaultValue), w.GetType().Name + " sets its own Style");
                Check.False(w.Resources.Keys.OfType<Type>().Any(t => typeof(Window).IsAssignableFrom(t)), w.GetType().Name + " has a Window-typed implicit style in its resources");
            }
            Check.True(built.Count >= 3);
        }
        finally
        {
            foreach (var w in built) { try { w.Close(); } catch { } }
            app.MainWindow = priorMain;
        }
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
