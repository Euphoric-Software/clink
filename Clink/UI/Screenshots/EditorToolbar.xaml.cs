using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clink.Screenshots.Editing;

namespace Clink.UI.Screenshots;

/// <summary>
/// The tool strip of the screenshot editor: tool pills, undo / redo, zoom and copy / save; a row of effects (blur, pixelate, highlight,
/// spotlight, magnify, erase), Insert image and the Adjust flyout; and a row with the options of the current tool (colour, width, text
/// size / bold / backdrop, and each effect's own). A view of an <see cref="EditorSession"/>: it shows the session's tool and options
/// and changes them through it; with an object selected the options show that object's. Every control has an automation name and a
/// tooltip with its key.
/// </summary>
public partial class EditorToolbar : UserControl
{
    private static readonly (string Name, string Hex)[] s_swatches =
    {
        ("Red", "#E53935"), ("Orange", "#FB8C00"), ("Yellow", "#FDD835"), ("Green", "#43A047"),
        ("Blue", "#1E88E5"), ("Purple", "#8E24AA"), ("White", "#FFFFFF"), ("Black", "#263238"),
    };

    private static readonly (string Name, string Hex)[] s_highlightSwatches =
    {
        ("Yellow", "#FFEB3B"), ("Green", "#76FF03"), ("Pink", "#FF4081"), ("Orange", "#FFA726"), ("Cyan", "#40C4FF"), ("Purple", "#E040FB"),
    };

    private static readonly (string Name, string Hex)[] s_outlineSwatches =
    {
        ("White", "#FFFFFF"), ("Black", "#000000"), ("Red", "#E53935"), ("Yellow", "#FFEB3B"), ("Blue", "#1E88E5"),
    };

    private EditorSession? _session;
    private bool _updating;
    private readonly List<(RadioButton Button, Color Color)> _swatches = new();
    private readonly List<Action> _refreshers = new();
    private long _popupClosedAt;

    // the panels' pieces that come and go
    private FrameworkElement _hlWidth = null!, _spotFalloff = null!;
    private Slider _brightness = null!, _contrast = null!, _sharpness = null!;
    private TextBlock _brightnessText = null!, _contrastText = null!, _sharpnessText = null!;

    internal event Action? CopyRequested, SaveRequested, FitRequested, ActualRequested, FocusCanvasRequested, InsertRequested;

    private enum Kind { Style, Text, Crop, Blur, Pixelate, Highlight, Spotlight, Magnify, Erase, Image }

    public EditorToolbar()
    {
        InitializeComponent();
        foreach (var (name, hex) in s_swatches)
        {
            ScreenshotEditorOptions.TryParseColor(hex, out var color);
            var b = MakeSwatch("EditorColor", name, color);
            b.Click += (_, _) => { _session?.SetColor(color); FocusCanvasRequested?.Invoke(); };
            _swatches.Add((b, color));
            SwatchPanel.Children.Add(b);
        }
        BuildEffectPanels();
        BuildAdjustPopup();
    }

    private RadioButton MakeSwatch(string group, string name, Color color) => MakeSwatchCore(group, name, color);

    private RadioButton MakeSwatchCore(string group, string name, Color color)
    {
        var b = new RadioButton
        {
            Style = (Style)FindResource("SwatchRadio"),
            GroupName = group,
            Background = new SolidColorBrush(color),
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 2, 0),
            ToolTip = name,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, name + " colour");
        return b;
    }

    // ------------------------------------------------------------------ building the option panels

    private TextBlock Label(string text, double left = 0) =>
        new() { Style = (Style)FindResource("MutedText"), Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 8, 0), TextWrapping = TextWrapping.NoWrap };

    /// <summary>A labelled slider with a value readout. <paramref name="apply"/> gets the value and whether the gesture is over (false while the thumb is held: a preview only).</summary>
    private FrameworkElement AddSlider(Panel host, string label, double min, double max, double step, string name, string tip, Func<double, string> format, Func<double> read, Action<double, bool> apply, double left = 12)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0) };
        row.Children.Add(Label(label));
        var slider = new Slider { Minimum = min, Maximum = max, Width = 110, IsSnapToTickEnabled = true, TickFrequency = step, SmallChange = step, LargeChange = step * 4, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(slider, name);
        var text = new TextBlock { Style = (Style)FindResource("MutedText"), Width = 44, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = format(e.NewValue);
            if (_updating || _session == null) return;
            apply(e.NewValue, !slider.IsMouseCaptureWithin);
        };
        slider.LostMouseCapture += Slider_LostCapture;
        row.Children.Add(slider);
        row.Children.Add(text);
        host.Children.Add(row);
        _refreshers.Add(() => { slider.Value = Math.Clamp(read(), min, max); text.Text = format(slider.Value); });
        return row;
    }

    private void AddCheck(Panel host, string content, string name, string tip, Func<bool> read, Action<bool> apply, double left = 12)
    {
        var box = new CheckBox { Content = content, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0), ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        RoutedEventHandler h = (_, _) => { if (!_updating && _session != null) { apply(box.IsChecked == true); FocusCanvasRequested?.Invoke(); } };
        box.Checked += h;
        box.Unchecked += h;
        host.Children.Add(box);
        _refreshers.Add(() => box.IsChecked = read());
    }

    private void AddSwatches(Panel host, string group, (string Name, string Hex)[] palette, Func<Color> current, Action<Color> pick, double left = 0)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0) };
        var list = new List<(RadioButton, Color)>();
        foreach (var (name, hex) in palette)
        {
            ScreenshotEditorOptions.TryParseColor(hex, out var color);
            var b = MakeSwatchCore(group, name, color);
            b.Click += (_, _) => { if (_session != null) { pick(color); FocusCanvasRequested?.Invoke(); } };
            list.Add((b, color));
            strip.Children.Add(b);
        }
        host.Children.Add(strip);
        _refreshers.Add(() => { var cur = current(); foreach (var (b, c) in list) b.IsChecked = c.R == cur.R && c.G == cur.G && c.B == cur.B; });
    }

    private static string Px(double v) => $"{Math.Round(v)} px";
    private static string Pct(double v) => $"{Math.Round(v)}%";

    private void BuildEffectPanels()
    {
        EditorSession S() => _session!;
        AddSlider(BlurPanel, "Radius", 1, 40, 1, "Blur radius", "How strong the blur is, in picture pixels", Px, () => S().Options.BlurRadius, (v, f) => S().SetBlurRadius((int)v, f), left: 0);
        AddSlider(PixelatePanel, "Block", 2, 64, 1, "Pixelate block size", "Size of one square, in picture pixels", Px, () => S().Options.PixelateBlock, (v, f) => S().SetPixelateBlock((int)v, f), left: 0);

        AddSwatches(HighlightPanel, "EditorHighlight", s_highlightSwatches, () => S().Options.HighlightColorValue, c => S().SetHighlightColor(c));
        AddCheck(HighlightPanel, "Freehand", "Freehand highlight", "Draw the highlight with the mouse instead of a rectangle (with a rectangle, Shift keeps it square)", () => S().Options.HighlightFreehand, v => S().SetHighlightFreehand(v));
        _hlWidth = AddSlider(HighlightPanel, "Width", 2, 80, 1, "Highlighter width", "Width of the freehand highlighter, in picture pixels", Px, () => S().Options.HighlightWidth, (v, f) => S().SetHighlightWidth(v, f));

        AddSlider(SpotlightPanel, "Dim", 0, 95, 5, "Spotlight darkness", "How dark the outside of the box is", Pct, () => S().Options.SpotDim * 100, (v, f) => S().SetSpotDim(v / 100, f), left: 0);
        AddSlider(SpotlightPanel, "Blur", 0, 40, 1, "Spotlight blur radius", "How blurred the outside of the box is, in picture pixels (the strongest, with a gradient)", Px, () => S().Options.SpotRadius, (v, f) => S().SetSpotRadius((int)v, f));
        AddCheck(SpotlightPanel, "Gradient", "Spotlight gradient blur", "The blur grows with the distance from the box", () => S().Options.SpotGradient, v => S().SetSpotGradient(v));
        _spotFalloff = AddSlider(SpotlightPanel, "Falloff", 5, 100, 5, "Spotlight falloff", "Distance over which the blur reaches full strength, in % of the picture's shorter side", Pct, () => S().Options.SpotFalloffPercent, (v, f) => S().SetSpotFalloff((int)v, f));

        AddSlider(MagnifyPanel, "Zoom", 1.25, 8, 0.25, "Magnify zoom", "How much the copy is enlarged (the proportions of the source are kept)", v => $"{v:0.##}x", () => S().Options.MagnifyZoom, (v, f) => S().SetMagnifyZoom(v, f), left: 0);
        AddCheck(MagnifyPanel, "Outline", "Magnify outline", "A frame around the enlarged copy", () => S().Options.MagnifyOutline, v => S().SetMagnifyOutline(v));
        AddSwatches(MagnifyPanel, "EditorOutline", s_outlineSwatches, () => S().Options.MagnifyOutlineColorValue, c => S().SetMagnifyOutlineColor(c), left: 8);
        AddCheck(MagnifyPanel, "Smooth", "Magnify smoothing", "Smooth the enlarged copy instead of showing crisp pixels", () => S().Options.MagnifySmooth, v => S().SetMagnifySmooth(v));

        AddSlider(ImagePanel, "Opacity", 10, 100, 5, "Image opacity", "How opaque the inserted image is", Pct, () => S().Options.ImageOpacity * 100, (v, f) => S().SetImageOpacity(v / 100, f), left: 0);
    }

    private void BuildAdjustPopup()
    {
        AdjustGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AdjustGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AdjustGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Slider Row(int row, string label, double min, double max, string name, string tip, out TextBlock text)
        {
            AdjustGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Label(label);
            l.MinWidth = 70;
            l.Margin = new Thickness(0, 6, 8, 6);
            Grid.SetRow(l, row);
            var s = new Slider { Minimum = min, Maximum = max, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 10, MinWidth = 160, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetName(s, name);
            Grid.SetRow(s, row);
            Grid.SetColumn(s, 1);
            text = new TextBlock { Style = (Style)FindResource("MutedText"), Width = 44, TextAlignment = TextAlignment.Right, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 2);
            AdjustGrid.Children.Add(l);
            AdjustGrid.Children.Add(s);
            AdjustGrid.Children.Add(text);
            s.LostMouseCapture += Slider_LostCapture;
            s.ValueChanged += (_, _) => AdjustSliderChanged(s);
            return s;
        }
        _brightness = Row(0, "Brightness", -100, 100, "Brightness", "Brightness, -100 to 100", out _brightnessText);
        _contrast = Row(1, "Contrast", -100, 100, "Contrast", "Contrast, -100 to 100", out _contrastText);
        _sharpness = Row(2, "Sharpness", 0, 200, "Sharpness", "Sharpness (unsharp mask), 0 to 200 %", out _sharpnessText);
    }

    private static string Signed(double v) => v > 0 ? $"+{Math.Round(v)}" : $"{Math.Round(v)}";

    private void ShowAdjustText()
    {
        _brightnessText.Text = Signed(_brightness.Value);
        _contrastText.Text = Signed(_contrast.Value);
        _sharpnessText.Text = $"{Math.Round(_sharpness.Value)}%";
    }

    private void AdjustSliderChanged(Slider changed)
    {
        ShowAdjustText();
        if (_updating || _session == null) return;
        var v = new AdjustmentValues((int)_brightness.Value, (int)_contrast.Value, (int)_sharpness.Value);
        _session.SetAdjust(v, final: !changed.IsMouseCaptureWithin);
    }

    // ------------------------------------------------------------------ session

    internal void Attach(EditorSession session)
    {
        if (_session != null) _session.StateChanged -= Refresh;
        _session = session;
        session.StateChanged += Refresh;
        Refresh();
    }

    /// <summary>Shows the zoom as a percentage (physical pixels per picture pixel).</summary>
    internal void SetZoom(double zoom) => ZoomText.Text = $"{Math.Round(zoom * 100)}%";

    private static Kind KindFor(EditorSession s)
    {
        switch (s.Tool)
        {
            case EditorTool.Crop: return Kind.Crop;
            case EditorTool.Text: return Kind.Text;
            case EditorTool.Blur: return Kind.Blur;
            case EditorTool.Pixelate: return Kind.Pixelate;
            case EditorTool.Highlight: return Kind.Highlight;
            case EditorTool.Spotlight: return Kind.Spotlight;
            case EditorTool.Magnify: return Kind.Magnify;
            case EditorTool.Erase: return Kind.Erase;
        }
        if (s.Selected is TextNote) return Kind.Text;
        if (s.Tool == EditorTool.Select)
        {
            switch (s.Selected)
            {
                case BlurRegion { Pixelate: true }: return Kind.Pixelate;
                case BlurRegion: return Kind.Blur;
                case HighlightRect or HighlightStroke: return Kind.Highlight;
                case SpotlightBox: return Kind.Spotlight;
                case MagnifyBox: return Kind.Magnify;
                case SmartEraseRect: return Kind.Erase;
                case InsertedImage: return Kind.Image;
            }
        }
        return Kind.Style;
    }

    private static Visibility Show(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    private void Refresh()
    {
        var s = _session;
        if (s == null) return;
        _updating = true;
        try
        {
            foreach (var r in new[] { SelectTool, PenTool, LineTool, StraightArrowTool, ArrowTool, RectangleTool, TextTool, CropTool, BlurTool, PixelateTool, HighlightTool, SpotlightTool, MagnifyTool, EraseTool })
                r.IsChecked = (string?)r.Tag == s.Tool.ToString();
            UndoButton.IsEnabled = s.CanUndo;
            RedoButton.IsEnabled = s.CanRedo;

            var kind = KindFor(s);
            bool textMode = kind == Kind.Text, cropMode = kind == Kind.Crop;
            var shown = textMode ? s.Options.TextColorValue : s.Options.ColorValue;
            foreach (var (b, c) in _swatches) b.IsChecked = c == shown;
            SwatchPanel.Visibility = Show(kind == Kind.Style || kind == Kind.Text);
            WidthPanel.Visibility = Show(kind == Kind.Style);
            TextPanel.Visibility = Show(textMode);
            ResetCropButton.Visibility = Show(cropMode);
            ResetCropButton.IsEnabled = s.State.Crop != s.State.Base.Bounds;
            BlurPanel.Visibility = Show(kind == Kind.Blur);
            PixelatePanel.Visibility = Show(kind == Kind.Pixelate);
            HighlightPanel.Visibility = Show(kind == Kind.Highlight);
            SpotlightPanel.Visibility = Show(kind == Kind.Spotlight);
            MagnifyPanel.Visibility = Show(kind == Kind.Magnify);
            ErasePanel.Visibility = Show(kind == Kind.Erase);
            ImagePanel.Visibility = Show(kind == Kind.Image);
            // the freehand width only matters for a freehand stroke (the tool in freehand mode, or a stroke selected); the falloff only for a gradient
            _hlWidth.Visibility = Show(s.Options.HighlightFreehand || s.Selected is HighlightStroke);
            bool gradient = s.Selected is SpotlightBox sp && s.Tool == EditorTool.Select ? sp.Gradient : s.Options.SpotGradient;
            _spotFalloff.Visibility = Show(gradient);

            WidthSlider.Value = s.Options.Width;
            WidthText.Text = $"{Math.Round(s.Options.Width)} px";
            FontSlider.Value = s.Options.FontSize;
            FontText.Text = $"{Math.Round(s.Options.FontSize)}";
            BoldCheck.IsChecked = s.Options.Bold;
            BackdropNone.IsChecked = s.BackdropOption == TextBackdrop.None;
            BackdropBox.IsChecked = s.BackdropOption == TextBackdrop.Box;
            BackdropOutline.IsChecked = s.BackdropOption == TextBackdrop.Outline;

            // the selected raster object shows its own settings, not the last ones used
            var shownOptions = SelectedAsOptions(s);
            foreach (var refresh in _refreshers) refresh();
            if (shownOptions != null) ApplySelectedValues(shownOptions);

            var adj = s.State.Adjust;
            _brightness.Value = adj.Brightness;
            _contrast.Value = adj.Contrast;
            _sharpness.Value = adj.Sharpness;
            ShowAdjustText();
            ResetAdjustButton.IsEnabled = !adj.IsIdentity;
            AdjustButton.FontWeight = adj.IsIdentity ? FontWeights.Normal : FontWeights.SemiBold;
        }
        finally { _updating = false; }
    }

    // The option panels read s.Options; when an object is selected they should show the object's values. Done by setting the sliders afterwards.
    private object? SelectedAsOptions(EditorSession s) => s.Tool == EditorTool.Select ? s.Selected : null;

    private void ApplySelectedValues(object selected)
    {
        // each panel's sliders were refreshed from the options; the selected object's own values replace them where it has them
        switch (selected)
        {
            case SpotlightBox sp:
                SetSliderIn(SpotlightPanel, 0, sp.Dim * 100);
                SetSliderIn(SpotlightPanel, 1, sp.Radius);
                SetCheckIn(SpotlightPanel, 0, sp.Gradient);
                SetSliderIn(SpotlightPanel, 2, 100.0 * sp.FalloffPx / Math.Max(1, Math.Min(_session!.State.Base.Width, _session.State.Base.Height)));
                break;
            case BlurRegion { Pixelate: false } b: SetSliderIn(BlurPanel, 0, b.Radius); break;
            case BlurRegion { Pixelate: true } b: SetSliderIn(PixelatePanel, 0, b.Block); break;
            case HighlightStroke hs: SetSliderIn(HighlightPanel, 0, hs.Width); break;
            case MagnifyBox m when m.Source.Width > 0: SetSliderIn(MagnifyPanel, 0, Math.Round(m.Dest.Width / (double)m.Source.Width * 4) / 4); SetCheckIn(MagnifyPanel, 0, m.OutlineWidth > 0); SetCheckIn(MagnifyPanel, 1, m.Smooth); break;
            case InsertedImage i: SetSliderIn(ImagePanel, 0, i.Opacity * 100); break;
        }
    }

    private static void SetSliderIn(Panel panel, int n, double value)
    {
        int k = 0;
        foreach (var child in panel.Children)
        {
            if (child is StackPanel row && row.Children.OfType<Slider>().FirstOrDefault() is { } slider)
            {
                if (k++ == n) { slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum); return; }
            }
        }
    }

    private static void SetCheckIn(Panel panel, int n, bool value)
    {
        int k = 0;
        foreach (var child in panel.Children)
            if (child is CheckBox box && k++ == n) { box.IsChecked = value; return; }
    }

    // ------------------------------------------------------------------ events

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null && sender is RadioButton { Tag: string tag } && Enum.TryParse<EditorTool>(tag, out var tool)) _session.SetTool(tool);
        FocusCanvasRequested?.Invoke();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { _session?.Undo(); FocusCanvasRequested?.Invoke(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _session?.Redo(); FocusCanvasRequested?.Invoke(); }
    private void Fit_Click(object sender, RoutedEventArgs e) { FitRequested?.Invoke(); FocusCanvasRequested?.Invoke(); }
    private void Actual_Click(object sender, RoutedEventArgs e) { ActualRequested?.Invoke(); FocusCanvasRequested?.Invoke(); }
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();
    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();
    private void ResetCrop_Click(object sender, RoutedEventArgs e) { _session?.ResetCrop(); FocusCanvasRequested?.Invoke(); }
    private void Insert_Click(object sender, RoutedEventArgs e) => InsertRequested?.Invoke();

    private void Adjust_Click(object sender, RoutedEventArgs e)
    {
        // the click that closed the flyout (a click outside it closes it first) must not open it again
        if (Environment.TickCount64 - _popupClosedAt < 250) return;
        AdjustPopup.IsOpen = true;
    }

    private void AdjustPopup_Closed(object? sender, EventArgs e)
    {
        _popupClosedAt = Environment.TickCount64;
        _session?.CommitPreview();
        FocusCanvasRequested?.Invoke();
    }

    private void ResetAdjust_Click(object sender, RoutedEventArgs e) { _session?.ResetAdjust(); }

    private void Width_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _session == null) return;
        _session.SetWidth(e.NewValue, final: !WidthSlider.IsMouseCaptureWithin);
    }

    private void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || _session == null) return;
        _session.SetFontSize(e.NewValue, final: !FontSlider.IsMouseCaptureWithin);
    }

    /// <summary>The slider was let go: the previewed width / size / value becomes one undo step.</summary>
    private void Slider_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _session?.CommitPreview();
        if (!AdjustPopup.IsOpen) FocusCanvasRequested?.Invoke();
    }

    private void Bold_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || _session == null) return;
        _session.SetBold(BoldCheck.IsChecked == true);
    }

    private void Backdrop_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null && sender is RadioButton { Tag: string tag } && Enum.TryParse<TextBackdrop>(tag, out var b)) _session.SetBackdrop(b);
        FocusCanvasRequested?.Invoke();
    }
}
