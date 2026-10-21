using System.Windows;
using System.Windows.Threading;
using Clink.Services;

namespace Clink.UI;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(7) };
    private ClipEntry? _entry;
    private bool _busy;

    public ToastWindow()
    {
        InitializeComponent();
        _hide.Tick += (_, _) => { if (!_busy) Hide(); };
        MouseEnter += (_, _) => _hide.Stop();
        MouseLeave += (_, _) => { if (!_busy) _hide.Start(); };
    }

    internal void Show(ClipEntry entry)
    {
        _entry = entry;
        string game = string.IsNullOrEmpty(entry.Game) ? "" : entry.Game + " · ";
        Subtitle.Text = $"{game}{TimeSpan.FromSeconds(entry.Duration):m\\:ss} · {entry.Size / 1_048_576.0:0.0} MB";
        ShareButton.Content = string.IsNullOrEmpty(entry.CopiumLink) ? "Share to Copium" : "Copy link";
        ShareButton.IsEnabled = true;
        var wa = SystemParameters.WorkArea;
        Show();
        UpdateLayout();
        Left = wa.Right - ActualWidth - 16;
        Top = wa.Bottom - ActualHeight - 16;
        _hide.Stop();
        _hide.Start();
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_entry == null || _busy) return;
        _busy = true;
        _hide.Stop();
        ShareButton.IsEnabled = false;
        ShareButton.Content = "Uploading…";
        var progress = new Progress<double>(p => ShareButton.Content = $"Uploading… {p:P0}");
        bool ok = await AppController.Instance.ShareAsync(_entry, progress);
        ShareButton.Content = ok ? "Link copied ✓" : "Upload failed";
        ShareButton.IsEnabled = !ok;
        _busy = false;
        _hide.Interval = TimeSpan.FromSeconds(ok ? 3 : 8);
        _hide.Start();
        _hide.Interval = TimeSpan.FromSeconds(7);
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_entry == null) return;
        Hide();
        (System.Windows.Application.Current.MainWindow as MainWindow)?.OpenTrim(_entry);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_entry != null) AppController.Instance.OpenFolder(_entry.Path);
        Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
