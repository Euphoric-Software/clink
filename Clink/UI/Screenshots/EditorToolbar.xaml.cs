using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clink.Screenshots.Editing;

namespace Clink.UI.Screenshots;

/// <summary>
/// The tool strip of the screenshot editor: tool pills, undo / redo, zoom and copy / save, and a second row with the options of the
/// current tool (colour, width, text size / bold / backdrop). A view of an <see cref="EditorSession"/>: it shows the session's
/// tool and options and changes them through it. Every control has an automation name and a tooltip with its key.
/// </summary>
public partial class EditorToolbar : UserControl
{
    private static readonly (string Name, string Hex)[] s_swatches =
    {
        ("Red", "#E53935"), ("Orange", "#FB8C00"), ("Yellow", "#FDD835"), ("Green", "#43A047"),
        ("Blue", "#1E88E5"), ("Purple", "#8E24AA"), ("White", "#FFFFFF"), ("Black", "#263238"),
    };

    private EditorSession? _session;
    private bool _updating;
    private readonly List<(RadioButton Button, Color Color)> _swatches = new();

    internal event Action? CopyRequested, SaveRequested, FitRequested, ActualRequested, FocusCanvasRequested;

    public EditorToolbar()
    {
        InitializeComponent();
        foreach (var (name, hex) in s_swatches)
        {
            ScreenshotEditorOptions.TryParseColor(hex, out var color);
            var b = new RadioButton
            {
                Style = (Style)FindResource("SwatchRadio"),
                GroupName = "EditorColor",
                Background = new SolidColorBrush(color),
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 2, 0),
                ToolTip = name,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, name + " colour");
            b.Click += (_, _) => { _session?.SetColor(color); FocusCanvasRequested?.Invoke(); };
            _swatches.Add((b, color));
            SwatchPanel.Children.Add(b);
        }
    }

    internal void Attach(EditorSession session)
    {
        if (_session != null) _session.StateChanged -= Refresh;
        _session = session;
        session.StateChanged += Refresh;
        Refresh();
    }

    /// <summary>Shows the zoom as a percentage (physical pixels per picture pixel).</summary>
    internal void SetZoom(double zoom) => ZoomText.Text = $"{Math.Round(zoom * 100)}%";

    private void Refresh()
    {
        var s = _session;
        if (s == null) return;
        _updating = true;
        try
        {
            foreach (var r in new[] { SelectTool, PenTool, LineTool, StraightArrowTool, ArrowTool, RectangleTool, TextTool, CropTool })
                r.IsChecked = (string?)r.Tag == s.Tool.ToString();
            UndoButton.IsEnabled = s.CanUndo;
            RedoButton.IsEnabled = s.CanRedo;

            bool textSelected = s.Selected is TextNote;
            bool textMode = s.Tool == EditorTool.Text || textSelected;
            bool cropMode = s.Tool == EditorTool.Crop;
            var shown = textMode ? s.Options.TextColorValue : s.Options.ColorValue;
            foreach (var (b, c) in _swatches) b.IsChecked = c == shown;
            SwatchPanel.Visibility = cropMode ? Visibility.Collapsed : Visibility.Visible;
            WidthPanel.Visibility = cropMode || textMode ? Visibility.Collapsed : Visibility.Visible;
            TextPanel.Visibility = textMode && !cropMode ? Visibility.Visible : Visibility.Collapsed;
            ResetCropButton.Visibility = cropMode ? Visibility.Visible : Visibility.Collapsed;
            ResetCropButton.IsEnabled = s.State.Crop != s.State.Base.Bounds;

            WidthSlider.Value = s.Options.Width;
            WidthText.Text = $"{Math.Round(s.Options.Width)} px";
            FontSlider.Value = s.Options.FontSize;
            FontText.Text = $"{Math.Round(s.Options.FontSize)}";
            BoldCheck.IsChecked = s.Options.Bold;
            BackdropNone.IsChecked = s.BackdropOption == TextBackdrop.None;
            BackdropBox.IsChecked = s.BackdropOption == TextBackdrop.Box;
            BackdropOutline.IsChecked = s.BackdropOption == TextBackdrop.Outline;
        }
        finally { _updating = false; }
    }

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

    /// <summary>The slider was let go: the previewed width / size becomes one undo step.</summary>
    private void Slider_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _session?.CommitPreview();
        FocusCanvasRequested?.Invoke();
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
