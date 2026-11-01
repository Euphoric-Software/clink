using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.UI;

/// <summary>
/// The replay-length slider (docs/SPEC-v2.md §2): 5 s – 60 min on a log scale so 15 s – 2 min has room, ticks at
/// 15 s / 30 s / 1 / 2 / 5 / 10 / 30 / 60 min, the 30–90 s "ideal zone" drawn brighter, and the value label ("1:30") at the
/// right. <see cref="ValueChanged"/> fires while dragging, <see cref="ValueCommitted"/> when the mouse is released or a key was
/// pressed. <see cref="Limit"/> is the longest buffer the machine can hold (set by the window from the bitrate and the RAM);
/// values above it are clamped.
/// </summary>
internal sealed class ReplayLengthSlider : FrameworkElement
{
    public const int Minimum = 5, Maximum = 3600;
    public const int IdealMin = 30, IdealMax = 90;
    private static readonly int[] s_ticks = { 15, 30, 60, 120, 300, 600, 1800, 3600 };

    /// <summary>A tick's caption in the active language: "15 s", "30 s", "1 m" ... "30 m", "1 h" (whole units from the catalog).</summary>
    internal static string TickLabel(int seconds) =>
        seconds < 60 ? L.T("unit|{n} s", ("n", seconds))
        : seconds < 3600 ? L.T("unit|{n} m", ("n", seconds / 60))
        : L.T("unit|{n} h", ("n", seconds / 3600));
    private const double MinLabelWidth = 50, PadLeft = 7, PadRight = 10;
    private const double TrackY = 22, TrackHeight = 4, ThumbRadius = 7, ControlHeight = 50;
    private static readonly double s_logMin = Math.Log(Minimum), s_logSpan = Math.Log(Maximum) - Math.Log(Minimum);

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(ReplayLengthSlider),
        new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((ReplayLengthSlider)d).ValueChanged?.Invoke(((ReplayLengthSlider)d).Value), (d, v) => ((ReplayLengthSlider)d).Coerce((int)v)));

    private int _limit = Maximum;
    private bool _dragging;

    public ReplayLengthSlider()
    {
        FlowDirection = FlowDirection.LeftToRight;   // the scale runs left to right in every language (a local value: the window root is right-to-left)
        Focusable = true;
        FocusVisualStyle = null;   // keyboard focus is drawn by OnRender (accent ring on the thumb), not as a rectangle around the row
        Height = ControlHeight;
        Cursor = Cursors.Hand;
        ToolTip = L.T("Drag, or use ← → (PageUp / PageDown for bigger steps)");
        // Everything is drawn in OnRender with the brushes of the moment: a theme change draws it again. The Capture tab is unloaded
        // while another tab is showing (the App tab, where the theme is changed), so it also draws again whenever it is loaded.
        Loaded += (_, _) => { ThemeManager.ThemeChanged -= InvalidateVisual; ThemeManager.ThemeChanged += InvalidateVisual; InvalidateVisual(); };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= InvalidateVisual;
    }

    /// <summary>Replay length in seconds.</summary>
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>Longest length the buffer can hold (RAM); above it the value is clamped. <see cref="Maximum"/> = no limit.</summary>
    public int Limit
    {
        get => _limit;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (v == _limit) return;
            _limit = v;
            if (Value > v) { Value = v; ValueCommitted?.Invoke(Value); }
            InvalidateVisual();
        }
    }

    /// <summary>Every change (including while dragging).</summary>
    public event Action<int>? ValueChanged;
    /// <summary>After the user let go of the thumb / pressed a key: the value to save.</summary>
    public event Action<int>? ValueCommitted;

    private int Coerce(int v) => Math.Clamp(v, Minimum, Math.Max(Minimum, _limit));

    // ------------------------------------------------------------------ scale

    /// <summary>0..1 position on the track of a length.</summary>
    public static double Fraction(int seconds) => (Math.Log(Math.Clamp(seconds, Minimum, Maximum)) - s_logMin) / s_logSpan;

    /// <summary>Length at a 0..1 position, snapped to a step that suits its magnitude (1 s below a minute, 5 s to 5 min, 15 s to 15 min, 30 s to 30 min, then 1 min).</summary>
    public static int SecondsAt(double fraction)
    {
        double raw = Math.Exp(s_logMin + Math.Clamp(fraction, 0, 1) * s_logSpan);
        int step = StepFor((int)Math.Round(raw));
        return Math.Clamp((int)Math.Round(raw / step) * step, Minimum, Maximum);
    }

    public static int StepFor(int seconds) => seconds < 60 ? 1 : seconds < 300 ? 5 : seconds < 900 ? 15 : seconds < 1800 ? 30 : 60;

    private double TrackLeft => PadLeft + ThumbRadius;
    private double TrackRight => Math.Max(TrackLeft + 10, ActualWidth - LabelWidth() - PadRight - ThumbRadius);

    private readonly List<(double X, double Width)> _drawnCaptions = new();
    private double _labelWidth = MinLabelWidth;
    private string? _labelWidthCode;     // the language and the scale the width was worked out for (compared, not concatenated: no string per call)
    private double _labelWidthPpd;

    /// <summary>
    /// The room kept at the right for the value label: 50, or the widest value the language can write ("1 h", "59 m 59 s" ...) plus a little, so
    /// a longer language does not run the label over the track (English stays at 50). Worked out once per language.
    /// </summary>
    internal double LabelWidthPx => LabelWidth();

    /// <summary>The tick captions the last render drew (left edge and width), for a test: a caption that would run into its neighbour is left out.</summary>
    internal IReadOnlyList<(double X, double Width)> DrawnCaptions => _drawnCaptions;

    private double LabelWidth()
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string code = L.Code;
        if (_labelWidthPpd == ppd && string.Equals(_labelWidthCode, code, StringComparison.Ordinal)) return _labelWidth;
        _labelWidthCode = code;
        _labelWidthPpd = ppd;
        double widest = 0;
        var family = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        foreach (int seconds in new[] { Minimum, 59, 885, 1770, Maximum })   // "59 s", "14:45", "29:30", "1 h": the widest shapes a value takes
            widest = Math.Max(widest, new FormattedText(Capabilities.FormatDuration(seconds), L.UiCulture, FlowDirection.LeftToRight, bold, 13, Brushes.Black, ppd).WidthIncludingTrailingWhitespace);
        _labelWidth = Math.Max(MinLabelWidth, Math.Ceiling(widest) + 6);
        return _labelWidth;
    }
    private double XOf(int seconds) => TrackLeft + Fraction(seconds) * (TrackRight - TrackLeft);
    private double FractionAt(double x) => Math.Clamp((x - TrackLeft) / (TrackRight - TrackLeft), 0, 1);

    // ------------------------------------------------------------------ input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _dragging = true;
        CaptureMouse();
        Value = SecondsAt(FractionAt(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed) Value = SecondsAt(FractionAt(e.GetPosition(this).X));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        ValueCommitted?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) { _dragging = false; ValueCommitted?.Invoke(Value); }
    }

    // No OnMouseWheel on purpose: the slider sits in the middle of the scrolling Capture tab, so a wheel notch while the row passes
    // under the pointer must keep scrolling the pane; a step here would also save the length and restart the engine, dropping the
    // buffered replay. The length is set by dragging or with the keys below, as the tooltip says.

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: case Key.Down: Step(-1, false); break;
            case Key.Right: case Key.Up: Step(1, false); break;
            case Key.PageDown: Step(-1, true); break;
            case Key.PageUp: Step(1, true); break;
            case Key.Home: Value = Minimum; ValueCommitted?.Invoke(Value); break;
            case Key.End: Value = Maximum; ValueCommitted?.Invoke(Value); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Step(int direction, bool big)
    {
        int v = Value;
        int step = StepFor(direction < 0 ? v - 1 : v) * (big ? 10 : 1);
        int next = direction < 0 ? (v - 1) / step * step : (v / step + 1) * step;
        Value = Math.Clamp(next, Minimum, Maximum);
        ValueCommitted?.Invoke(Value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); InvalidateVisual(); }

    // ------------------------------------------------------------------ render

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, ControlHeight);

    // The fallbacks are the Dark / Spark Blue palette (UI/Palette.xaml), for a control that is drawn where the application's resources are not.
    private Brush Res(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        // Hit-test surface (the whole control, including the label), so a click anywhere on the row moves the thumb.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var track = Res("Panel3", Color.FromRgb(0x3B, 0x3E, 0x4B));
        var zone = Res("AccentDim", Color.FromRgb(0x3D, 0x5A, 0x78));
        var fg = Res("Fg", Color.FromRgb(0xE9, 0xEB, 0xF2));
        var muted = Res("Muted", Color.FromRgb(0x9E, 0xA4, 0xB3));
        var accent = Res("Accent", Color.FromRgb(0x6F, 0x9C, 0xC4));
        var accent2 = Res("Accent2", Color.FromRgb(0x8E, 0xB1, 0xD2));   // the "ideal" caption: text, so Accent2 (the accent as text fails 4.5:1 on the light theme for several accents)
        var danger = Res("Danger", Color.FromRgb(0xF2, 0x6B, 0x6B));
        var overLimit = Res("DangerTint", Color.FromArgb(0x70, 0xF2, 0x6B, 0x6B));
        double left = TrackLeft, right = TrackRight;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var family = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        // track, the ideal zone on top, and the part the RAM limit cuts off
        dc.DrawRoundedRectangle(track, null, new Rect(left, TrackY - TrackHeight / 2, right - left, TrackHeight), 2, 2);
        double zl = XOf(IdealMin), zr = XOf(IdealMax);
        dc.DrawRoundedRectangle(zone, null, new Rect(zl, TrackY - TrackHeight / 2 - 1, zr - zl, TrackHeight + 2), 3, 3);
        if (_limit < Maximum)
        {
            double xl = XOf(_limit);
            dc.DrawRectangle(overLimit, null, new Rect(xl, TrackY - TrackHeight / 2, Math.Max(0, right - xl), TrackHeight));
        }
        var ideal = new FormattedText(L.T("replay-length|ideal"), L.UiCulture, FlowDirection.LeftToRight, face, 10, accent2, ppd);
        dc.DrawText(ideal, new Point((zl + zr) / 2 - ideal.Width / 2, TrackY - TrackHeight / 2 - 4 - ideal.Height));

        // ticks + labels. The ticks are always drawn; a caption that would run into the one before it (a longer language) is left out, the first and the last stay.
        var tickPen = new Pen(muted, 1) { StartLineCap = PenLineCap.Flat };
        var captions = new List<(double X, FormattedText Text)>();
        for (int i = 0; i < s_ticks.Length; i++)
        {
            double x = Math.Round(XOf(s_ticks[i])) + 0.5;
            dc.DrawLine(tickPen, new Point(x, TrackY + TrackHeight / 2 + 3), new Point(x, TrackY + TrackHeight / 2 + 7));
            var ft = new FormattedText(TickLabel(s_ticks[i]), L.UiCulture, FlowDirection.LeftToRight, face, 10, muted, ppd);
            double tx = i == 0 ? Math.Max(left - ThumbRadius, x - ft.Width / 2) : i == s_ticks.Length - 1 ? Math.Min(right + ThumbRadius - ft.Width, x - ft.Width / 2) : x - ft.Width / 2;
            captions.Add((tx, ft));
        }
        const double CaptionGap = 4;
        double lastEnd = double.NegativeInfinity, lastStart = captions[^1].X;
        _drawnCaptions.Clear();
        for (int i = 0; i < captions.Count; i++)
        {
            var (cx, cft) = captions[i];
            bool edge = i == 0 || i == captions.Count - 1;
            if (!edge && (cx < lastEnd + CaptionGap || cx + cft.Width + CaptionGap > lastStart)) continue;
            dc.DrawText(cft, new Point(cx, TrackY + TrackHeight / 2 + 8));
            _drawnCaptions.Add((cx, cft.Width));
            lastEnd = cx + cft.Width;
        }

        // thumb
        double tx0 = XOf(Value);
        bool hot = IsMouseOver || IsKeyboardFocused || _dragging;
        dc.DrawEllipse(Value > _limit ? danger : fg, hot ? new Pen(accent, 2) : null, new Point(tx0, TrackY), ThumbRadius, ThumbRadius);

        // value label at the right
        var val = new FormattedText(Capabilities.FormatDuration(Value), L.UiCulture, FlowDirection.LeftToRight, bold, 13, fg, ppd) { TextAlignment = TextAlignment.Right };
        dc.DrawText(val, new Point(w - PadRight + 4, TrackY - val.Height / 2));
    }
}
