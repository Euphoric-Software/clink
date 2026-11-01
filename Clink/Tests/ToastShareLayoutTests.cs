using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// The split Share button on the clip-saved toast: the row fits the 330 px card (laid out offscreen, never shown), a picker that throws
/// leaves the toast usable, and (only with CLINK_SHOTS=1) offscreen pictures of the split button for a visual check.
/// </summary>
internal static class ToastShareLayoutTests
{
    private const double CardWidth = 330;
    private const double RequiredSlack = 12;

    /// <summary>
    /// Builds the real ToastWindow (never shown: no handle, nothing on screen) in the split state the constructor gives it when a token
    /// is set and cloud sharing is on, with <paramref name="label"/> on the main half. A split state is forced through
    /// <see cref="ToastWindow.ApplySplitLayout"/>, the very method the constructor calls, because the test has no Copium token.
    /// </summary>
    private static ToastWindow SplitToast(string label)
    {
        var toast = new ToastWindow();
        toast.ShareButton.Content = label;
        toast.ApplySplitLayout();
        return toast;
    }

    /// <summary>Measures and arranges the toast's card (the window's content, the real XAML) at the window's width, offscreen.</summary>
    private static void LayOut(ToastWindow toast)
    {
        var card = toast.Card;
        card.Measure(new Size(CardWidth, double.PositiveInfinity));
        card.Arrange(new Rect(0, 0, CardWidth, card.DesiredSize.Height));
        card.UpdateLayout();
    }

    /// <summary>The width the button row may use: the card minus its border and padding minus the row's own left margin.</summary>
    private static double RowAvailable(ToastWindow toast)
    {
        var card = toast.Card;
        return CardWidth - card.BorderThickness.Left - card.BorderThickness.Right - card.Padding.Left - card.Padding.Right - toast.ClipButtons.Margin.Left;
    }

    [UnitTest]
    public static void The_split_toast_button_row_fits_the_card_with_slack_for_Share_and_for_the_busy_status()
    {
        double mainWidth = 0;
        foreach (string label in new[] { "Share", ToastWindow.WidestShareStatus })
        {
            var toast = SplitToast(label);
            LayOut(toast);
            double available = RowAvailable(toast);
            double needed = toast.ClipButtons.DesiredSize.Width - toast.ClipButtons.Margin.Left;
            Check.True(needed <= available - RequiredSlack,
                $"split row with \"{label}\" needs {needed:0.0} px of {available:0.0} px (slack {available - needed:0.0}, wants {RequiredSlack:0})");
            // really arranged: Folder's right edge is inside the card's content area
            double folderRight = toast.FolderButton.TranslatePoint(new Point(toast.FolderButton.ActualWidth, 0), toast.Card).X;
            double contentRight = CardWidth - toast.Card.BorderThickness.Right - toast.Card.Padding.Right;
            Check.True(folderRight <= contentRight - RequiredSlack, $"Folder ends at {folderRight:0.0}, the content area at {contentRight:0.0} (\"{label}\")");
            if (label == "Share") mainWidth = toast.ShareButton.ActualWidth;
            else Check.Near(mainWidth, toast.ShareButton.ActualWidth, 0.01, "the main half does not grow from \"Share\" to \"Uploading… 100%\": the layout does not shift");
        }
    }

    [UnitTest]
    public static void The_plain_toast_button_row_is_unchanged()
    {
        var toast = new ToastWindow();
        Check.Equal(new Thickness(8, 0, 0, 0), toast.TrimButton.Margin, "Trim keeps its 8 px gap");
        Check.Equal(new Thickness(8, 0, 0, 0), toast.FolderButton.Margin, "Folder keeps its 8 px gap");
        Check.Equal(132.0, toast.ShareButton.MinWidth, "the plain button keeps MinWidth 132");
        Check.True(toast.ShareArrow.Visibility == Visibility.Collapsed, "no arrow");
    }

    [UnitTest]
    public static void A_picker_that_throws_leaves_the_toast_halves_enabled_and_labelled()
    {
        using var dir = new TempDir();
        string? savedOverride = Settings.DirOverride;
        Settings.DirOverride = dir.Path;
        try
        {
            string clips = dir.File("clips");
            Directory.CreateDirectory(clips);
            var settings = Settings.FromJson("{}");
            settings.ClipsFolder = clips;
            using var app = new AppController(settings, new Updater(settings));
            string path = dir.File("clip.mp4");
            File.WriteAllBytes(path, new byte[64]);
            var entry = app.Library.Add(path, 2, 64, null, Array.Empty<string>());
            app.SaveTargetPicker = _ => throw new InvalidOperationException("the dialog blew up");
            var toast = new ToastWindow(null!, "clip", entry, null, "Clip saved", "", TimeSpan.FromSeconds(7), false);
            string label = (string)toast.ShareButton.Content;
            var task = toast.RunChoiceAsync(ShareChoice.SaveCopy);
            ScreenshotUploadTests.Pump(() => task.IsCompleted, "the choice");
            task.GetAwaiter().GetResult();
            Check.Equal(label, (string)toast.ShareButton.Content, "the label is back (not \"Saving…\")");
            Check.True(toast.ShareButton.IsEnabled && toast.ShareArrow.IsEnabled, "both halves are enabled again");
            toast.Dismiss();   // stops the hide timer the catch restarted
        }
        finally { Settings.DirOverride = savedOverride; }
    }

    // ------------------------------------------------------------------ offscreen pictures (CLINK_SHOTS=1)

    private static string ShotsFolder()
    {
        if (Environment.GetEnvironmentVariable("CLINK_SHOTS_DIR") is { Length: > 0 } custom) return custom;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "dist")) && Directory.Exists(Path.Combine(d.FullName, "Clink")))
                return Path.Combine(d.FullName, "dist", "shots");
        throw new TestFailure("no dist folder above " + AppContext.BaseDirectory);
    }

    /// <summary>Lays <paramref name="element"/> out on a panel-coloured backdrop (measured and arranged, never shown) and writes it as a 1x PNG.</summary>
    private static string Shot(FrameworkElement element, string folder, string name, double? width = null)
    {
        var backdrop = new Border { Background = (Brush)Application.Current.Resources["Panel"], Padding = new Thickness(14), Child = element };
        if (width != null) backdrop.Width = width.Value;
        backdrop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        backdrop.Arrange(new Rect(backdrop.DesiredSize));
        backdrop.UpdateLayout();
        return Save(backdrop, folder, name);
    }

    private static string Save(FrameworkElement root, string folder, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string file = Path.Combine(folder, name + ".png");
        using (var stream = File.Create(file)) encoder.Save(stream);
        return file;
    }

    private static Button Half(string style, string text, Thickness padding, Thickness margin, double minWidth = 0) =>
        new() { Style = (Style)Application.Current.Resources[style], Content = text, Padding = padding, Margin = margin, MinWidth = minWidth };

    [UnitTest]
    public static void Offscreen_pictures_of_the_split_button_when_CLINK_SHOTS_is_set()
    {
        if (Environment.GetEnvironmentVariable("CLINK_SHOTS") != "1") return;   // a no-op pass otherwise
        string folder = ShotsFolder();
        Directory.CreateDirectory(folder);
        string theme0 = ThemeManager.Theme, accent0 = ThemeManager.Accent;
        try
        {
            foreach (string theme in new[] { "dark", "light" })
            {
                ThemeManager.Apply(theme, "spark");
                // the clip row's split button (ClipsPane.xaml: Padding 9,5, MinWidth 52, then the arrow)
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Half("AccentSplitMain", "Share", new Thickness(9, 5, 9, 5), new Thickness(0), 52));
                row.Children.Add(Half("AccentSplitArrow", ShareChoices.SplitArrowLabel, new Thickness(7, 5, 7, 5), new Thickness(0)));
                Shot(row, folder, $"clip-row-split-{theme}");
                // the bar's two halves (ClipsPane.Selection.cs: BarButton paddings and margins)
                var bar = new StackPanel { Orientation = Orientation.Horizontal };
                bar.Children.Add(Half("AccentSplitMain", "Share", new Thickness(9, 5, 9, 5), new Thickness(0, 0, 0, 2), 52));
                bar.Children.Add(Half("AccentSplitArrow", ShareChoices.SplitArrowLabel, new Thickness(7, 5, 7, 5), new Thickness(0, 0, 6, 2)));
                Shot(bar, folder, $"bar-split-{theme}");
                // the whole toast card (the real ToastWindow content, never shown) in split mode
                foreach (var (label, tag) in new[] { ("Share", "share"), (ToastWindow.WidestShareStatus, "uploading") })
                {
                    var toast = SplitToast(label);
                    toast.TitleText.Text = "Clip saved";
                    toast.Subtitle.Text = "Clip · 0:30 · 48 MB";
                    LayOut(toast);
                    Save(toast.Card, folder, $"toast-split-{tag}-{theme}");
                    if (theme == "dark")
                        File.AppendAllText(Path.Combine(folder, "widths.txt"), $"\"{label}\": row needs {toast.ClipButtons.DesiredSize.Width - toast.ClipButtons.Margin.Left:0.0} of {RowAvailable(toast):0.0} px; main half {toast.ShareButton.ActualWidth:0.0}\n");
                }
            }
        }
        finally { ThemeManager.Apply(theme0, accent0); }
    }
}
