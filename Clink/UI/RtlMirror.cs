using System.Windows;
using Clink.Services.Localization;
using System.Windows.Media;

namespace Clink.UI;

/// <summary>
/// Mirrors a glyph that points somewhere (a chevron that means "more", Undo, Redo) in a right-to-left layout. FlowDirection mirrors the
/// layout and the text, not an icon font's glyph: <c>l:RtlMirror.Glyph="True"</c> turns the element's render transform into a horizontal flip
/// while its effective FlowDirection is RightToLeft, and leaves it alone otherwise (so English is untouched and does nothing). It is applied when the property is set and when the element loads; the language is fixed per process,
/// so no FlowDirection change tracking is kept (a value-changed subscription would keep every element alive). Symmetric glyphs and glyphs that
/// are not directional (play, close, check, a folder) do not use it.
/// </summary>
internal static class RtlMirror
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(bool), typeof(RtlMirror), new PropertyMetadata(false, OnGlyphChanged));

    public static bool GetGlyph(DependencyObject d) => (bool)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, bool value) => d.SetValue(GlyphProperty, value);

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            if (!L.IsRtl) return;
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            fe.Loaded += OnLoaded;
            Update(fe);
        }
        else
        {
            fe.Loaded -= OnLoaded;
            fe.ClearValue(UIElement.RenderTransformProperty);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) { if (sender is FrameworkElement fe) Update(fe); }

    /// <summary>Sets the transform from the element's effective FlowDirection: a horizontal flip when right-to-left, nothing otherwise.</summary>
    internal static void Update(FrameworkElement fe)
    {
        if (!L.IsRtl) return;
        bool flip = fe.FlowDirection == FlowDirection.RightToLeft;
        if (flip) fe.RenderTransform = new ScaleTransform(-1, 1);
        else fe.ClearValue(UIElement.RenderTransformProperty);
    }
}
