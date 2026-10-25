using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Services;

namespace Clink.UI.Screenshots;

/// <summary>
/// A small dialog to choose the picture to insert into the screenshot: the recent screenshots from the history (worked out and checked
/// on a worker by <see cref="InsertPickerModel"/>, thumbnails decoded on a worker one after the other, newest first, the file being
/// edited left out), Browse… (PNG / JPG / BMP) and Paste. It only chooses: the editor session decodes (with the size limits) and
/// inserts. Escape closes it. Nothing is read from disk on the UI thread.
/// </summary>
public partial class InsertImagePicker : Window
{
    internal const int ThumbnailWidth = 160;

    private readonly InsertPickerModel _model;
    private bool _closed;

    /// <summary>The file chosen (a recent screenshot or Browse…); null when Paste was chosen or the dialog was cancelled.</summary>
    internal string? PickedPath { get; private set; }
    /// <summary>Paste was chosen.</summary>
    internal bool PasteChosen { get; private set; }

    internal InsertImagePicker(InsertPickerModel model, bool canPaste)
    {
        InitializeComponent();
        _model = model;
        PasteButton.IsEnabled = canPaste;
        Closed += (_, _) => _closed = true;
        Loaded += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        List<ScreenshotEntry> items;
        try { items = await _model.LoadAsync(); }
        catch (Exception ex)
        {
            Log.Warn("screenshot editor: recent screenshots: " + ex.Message);
            items = new List<ScreenshotEntry>();
        }
        if (_closed) return;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var tiles = new List<(Image Image, string Path)>();
        foreach (var e in items)
        {
            var image = new Image { Width = ThumbnailWidth, Height = 100, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var caption = new TextBlock
            {
                Style = (Style)FindResource("MutedText"),
                Text = $"{e.Created:MMM d, HH:mm}  ·  {e.Width} x {e.Height}",
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Width = ThumbnailWidth,
                Margin = new Thickness(0, 4, 0, 0),
            };
            var panel = new StackPanel();
            panel.Children.Add(new Border { Background = (Brush)FindResource("Panel2"), CornerRadius = new CornerRadius(4), Child = image });
            panel.Children.Add(caption);
            var button = new Button { Content = panel, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(6), Tag = e.Path, ToolTip = Path.GetFileName(e.Path) };
            System.Windows.Automation.AutomationProperties.SetName(button, "Insert " + Path.GetFileName(e.Path));
            string path = e.Path;
            button.Click += (_, _) => { PickedPath = path; DialogResult = true; };
            Tiles.Children.Add(button);
            tiles.Add((image, path));
        }
        // one worker decodes the thumbnails one after the other (a 4K PNG is not cheap); each appears when it is ready
        foreach (var (image, path) in tiles)
        {
            if (_closed) return;
            BitmapSource? thumb = null;
            try { thumb = await _model.ThumbnailAsync(path); }
            catch (Exception ex) { Log.Warn("screenshot editor: thumbnail: " + ex.Message); }
            if (_closed) return;
            if (thumb != null) image.Source = thumb;
        }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        PasteChosen = true;
        DialogResult = true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert image",
            Filter = "Pictures (PNG, JPG, BMP)|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        PickedPath = dialog.FileName;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
}
