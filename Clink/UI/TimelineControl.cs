using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Clink.UI;

/// <summary>
/// Editor-style timeline: a bar with the selected (kept) range, draggable in/out handles, a playhead that can be
/// scrubbed, time labels and keyframe ticks. Keyboard handling is left to the hosting window. With
/// <see cref="ShowSelection"/> off it is a plain seek bar: the played part, the playhead and the time labels, no range,
/// handles or keyframes.
/// </summary>
internal sealed class TimelineControl : FrameworkElement
{
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectionChanged));
    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnSelectionChanged));
    public static readonly DependencyProperty KeyframesProperty = DependencyProperty.Register(
        nameof(Keyframes), typeof(IList<double>), typeof(TimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowSelectionProperty = DependencyProperty.Register(
        nameof(ShowSelection), typeof(bool), typeof(TimelineControl), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double SelectionStart { get => (double)GetValue(SelectionStartProperty); set => SetValue(SelectionStartProperty, value); }
    public double SelectionEnd { get => (double)GetValue(SelectionEndProperty); set => SetValue(SelectionEndProperty, value); }
    public IList<double>? Keyframes { get => (IList<double>?)GetValue(KeyframesProperty); set => SetValue(KeyframesProperty, value); }
    /// <summary>
    /// True (the default): the range to keep, its two handles and the keyframe ticks are drawn and the handles can be dragged.
    /// False: a seek bar for playing a clip, where a click or drag anywhere only moves the playhead and the part already played is filled.
    /// </summary>
    public bool ShowSelection { get => (bool)GetValue(ShowSelectionProperty); set => SetValue(ShowSelectionProperty, value); }

    /// <summary>Raised when the user scrubs the playhead (the new position has already been applied).</summary>
    public event Action<double>? PositionSeeked;
    /// <summary>Raised whenever SelectionStart or SelectionEnd changes (user drag or programmatic).</summary>
    public event Action? SelectionChanged;

    /// <summary>True while the user is dragging the start or end handle.</summary>
    public bool IsDraggingHandle => _drag is Drag.Start or Drag.End;
    /// <summary>True while the user is scrubbing the playhead.</summary>
    public bool IsScrubbing => _drag == Drag.Scrub;
    /// <summary>Which handle is being dragged: 0 none, 1 start, 2 end.</summary>
    public int DraggingHandle => _drag == Drag.Start ? 1 : _drag == Drag.End ? 2 : 0;

    public const double MinSelection = 0.2;

    private const double PadX = 12, BarTop = 12, BarH = 28, HandleW = 10, HitTol = 7;
    private enum Drag { None, Start, End, Scrub }
    private Drag _drag;
    private double _grabOffset; // px from the pointer to the grabbed handle's centre, so a grab does not move it
    private bool _moved;

    public TimelineControl()
    {
        Height = 64;
        Focusable = false;
        ClipToBounds = true;
        SizeChanged += (_, _) => InvalidateVisual();
        // Everything is drawn in OnRender with the brushes of the moment: a theme change draws it again (and so does every load).
        Loaded += (_, _) => { ThemeManager.ThemeChanged -= InvalidateVisual; ThemeManager.ThemeChanged += InvalidateVisual; InvalidateVisual(); };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= InvalidateVisual;
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TimelineControl)d).SelectionChanged?.Invoke();

    // ------------------------------------------------------------------ geometry

    private double TrackWidth => Math.Max(1, ActualWidth - 2 * PadX);
    private double XOf(double t) => Duration > 0 ? PadX + Math.Clamp(t / Duration, 0, 1) * TrackWidth : PadX;
    private double TOf(double x) => Duration > 0 ? Math.Clamp((x - PadX) / TrackWidth, 0, 1) * Duration : 0;

    // ------------------------------------------------------------------ rendering

    // The fallbacks are the Dark / Cyan palette (UI/Palette.xaml), for a control that is drawn where the application's resources are not.
    private Brush B(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        // Transparent hit-test background so clicks anywhere in the control register.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        Brush panel2 = B("Panel2", Color.FromRgb(0x23, 0x2A, 0x37));
        Brush panel3 = B("Panel3", Color.FromRgb(0x2E, 0x36, 0x45));
        Brush border = B("Border", Color.FromRgb(0x2A, 0x32, 0x40));
        Brush fg = B("Fg", Color.FromRgb(0xEA, 0xF2, 0xFF));
        Brush muted = B("Muted", Color.FromRgb(0x8A, 0x94, 0xA6));
        Brush accent = B("Accent", Color.FromRgb(0x22, 0xD3, 0xEE));
        Brush accentDim = B("AccentDim", Color.FromRgb(0x1B, 0x6F, 0x84));
        Brush accent2 = B("Accent2", Color.FromRgb(0x3B, 0x82, 0xF6));
        Brush bg = B("Bg", Color.FromRgb(0x0A, 0x0E, 0x14));

        double dur = Duration;
        double tw = TrackWidth;
        var bar = new Rect(PadX, BarTop, tw, BarH);
        dc.DrawRoundedRectangle(panel2, new Pen(border, 1), bar, 4, 4);

        if (dur > 0)
        {
            bool select = ShowSelection;
            double xs = XOf(SelectionStart), xe = XOf(SelectionEnd);
            if (xe < xs) (xs, xe) = (xe, xs);
            if (!select) { xs = PadX + 1; xe = XOf(Position); }   // a seek bar: what has been played so far
            // Dimmed outside, highlighted inside (the range to keep, in AccentDim); a seek bar fills the part played in Accent2.
            dc.DrawRoundedRectangle(select ? accentDim : accent2, null, new Rect(xs, BarTop + 1, Math.Max(0, xe - xs), BarH - 2), 3, 3);

            // Keyframe ticks along the bottom edge of the bar.
            var keys = select ? Keyframes : null;
            if (keys != null && keys.Count > 0)
            {
                var tickPen = new Pen(muted, 1) { StartLineCap = PenLineCap.Flat };
                double lastX = -10;
                foreach (double k in keys)
                {
                    double x = Math.Round(XOf(k)) + 0.5;
                    if (x - lastX < 2) continue;
                    lastX = x;
                    dc.DrawLine(tickPen, new Point(x, BarTop + BarH - 5), new Point(x, BarTop + BarH - 1));
                }
            }

            // Time labels + ticks under the bar.
            double pxPerSec = tw / dur;
            double step = PickStep(pxPerSec);
            var labelPen = new Pen(panel3, 1);
            double labelY = BarTop + BarH + 4;
            var typeface = new Typeface(TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            for (double t = 0; t <= dur + 1e-6; t += step)
            {
                double x = Math.Round(XOf(t)) + 0.5;
                dc.DrawLine(labelPen, new Point(x, labelY), new Point(x, labelY + 4));
                var ft = new FormattedText(FormatTime(t, step), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, muted, dip);
                double tx = Math.Clamp(x - ft.Width / 2, 0, w - ft.Width);
                dc.DrawText(ft, new Point(tx, labelY + 5));
            }

            // Handles (centred on their time, a bit taller than the bar) with a grip line.
            if (select)
            {
                DrawHandle(dc, xs, accent, bg);
                DrawHandle(dc, xe, accent, bg);
            }

            // Playhead: 2 px line with a small triangle on top.
            double xp = Math.Round(XOf(Position));
            dc.DrawRectangle(fg, null, new Rect(xp - 1, BarTop - 6, 2, BarH + 9));
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(xp - 5, BarTop - 10), true, true);
                g.LineTo(new Point(xp + 5, BarTop - 10), false, false);
                g.LineTo(new Point(xp, BarTop - 4), false, false);
            }
            tri.Freeze();
            dc.DrawGeometry(fg, null, tri);
        }
    }

    private static void DrawHandle(DrawingContext dc, double x, Brush fill, Brush grip)
    {
        var r = new Rect(x - HandleW / 2, BarTop - 2, HandleW, BarH + 4);
        dc.DrawRoundedRectangle(fill, null, r, 3, 3);
        dc.DrawRectangle(grip, null, new Rect(x - 1, BarTop + BarH / 2 - 6, 2, 12));
    }

    private static double PickStep(double pxPerSec)
    {
        foreach (double s in new[] { 0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600 })
            if (s * pxPerSec >= 64) return s;
        return 600;
    }

    private static string FormatTime(double t, double step)
    {
        // Round to the displayed unit first so 59.96 becomes 1:00 rather than 0:60.
        if (step < 1)
        {
            long tenths = (long)Math.Round(t * 10, MidpointRounding.AwayFromZero);
            return $"{tenths / 600}:{(tenths % 600) / 10.0:00.0}";
        }
        long secs = (long)Math.Round(t, MidpointRounding.AwayFromZero);
        return $"{secs / 60}:{secs % 60:00}";
    }

    // ------------------------------------------------------------------ mouse

    private Drag HitTest(double x)
    {
        if (Duration <= 0 || !ShowSelection) return Drag.Scrub;
        double xs = XOf(SelectionStart), xe = XOf(SelectionEnd);
        double ds = Math.Abs(x - xs), de = Math.Abs(x - xe);
        if (ds <= HitTol && de <= HitTol) return ds <= de ? Drag.Start : Drag.End;
        if (ds <= HitTol) return Drag.Start;
        if (de <= HitTol) return Drag.End;
        return Drag.Scrub;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0) return;
        double x = e.GetPosition(this).X;
        _drag = HitTest(x);
        CaptureMouse();
        _moved = false;
        if (_drag == Drag.Scrub)
        {
            _grabOffset = 0;
            Apply(x); // a click on the bar seeks there
        }
        else
        {
            // A handle stays where it is until the pointer actually moves: applying the click position would snap the in
            // point back a keyframe or nudge the out point by the hit tolerance just for grabbing it.
            _grabOffset = XOf(_drag == Drag.Start ? SelectionStart : SelectionEnd) - x;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;
        if (_drag == Drag.None)
        {
            var hit = HitTest(x);
            Cursor = hit is Drag.Start or Drag.End ? Cursors.SizeWE : Cursors.Arrow;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        _moved = true;
        Apply(x + _grabOffset);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None) return;
        if (_drag == Drag.Scrub || _moved) Apply(e.GetPosition(this).X + _grabOffset);
        EndDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _drag = Drag.None;
    }

    private void EndDrag()
    {
        _drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Apply(double x)
    {
        double t = TOf(x);
        switch (_drag)
        {
            case Drag.Start:
            {
                double v = SnapToKeyframe(t);
                v = Math.Clamp(v, 0, Math.Max(0, SelectionEnd - MinSelection));
                if (v != SelectionStart) SelectionStart = v;
                break;
            }
            case Drag.End:
            {
                double v = Math.Clamp(t, Math.Min(Duration, SelectionStart + MinSelection), Duration);
                if (v != SelectionEnd) SelectionEnd = v;
                break;
            }
            case Drag.Scrub:
                Position = t;
                PositionSeeked?.Invoke(t);
                break;
        }
    }

    /// <summary>Nearest keyframe at or before <paramref name="t"/> (unchanged when there are no keyframes before it).</summary>
    public double SnapToKeyframe(double t)
    {
        var keys = Keyframes;
        if (keys == null || keys.Count == 0) return t;
        double best = double.NaN;
        foreach (double k in keys)
        {
            if (k <= t + 0.001 && (double.IsNaN(best) || k > best)) best = k;
        }
        return double.IsNaN(best) ? t : best;
    }
}
