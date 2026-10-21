using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Clink.Native;

namespace Clink.UI;

/// <summary>Mini map of the desktop: one rectangle per monitor; the captured one is highlighted. Click to choose.</summary>
internal sealed class MonitorMap : Canvas
{
    private List<MonitorInfo> _monitors = new();
    private string? _selected;
    public event Action<MonitorInfo>? Selected;

    public MonitorMap()
    {
        Height = 92;
        ClipToBounds = true;
        SizeChanged += (_, _) => Rebuild();
    }

    public void SetMonitors(List<MonitorInfo> monitors, string? selectedDevice)
    {
        _monitors = monitors;
        _selected = selectedDevice;
        Rebuild();
    }

    public void Select(string? device) { _selected = device; Rebuild(); }

    private void Rebuild()
    {
        Children.Clear();
        if (_monitors.Count == 0 || ActualWidth <= 0) return;
        int minX = _monitors.Min(m => m.Left), minY = _monitors.Min(m => m.Top);
        int maxX = _monitors.Max(m => m.Left + m.Width), maxY = _monitors.Max(m => m.Top + m.Height);
        double scale = Math.Min((ActualWidth - 8) / (maxX - minX), (Height - 8) / (maxY - minY));
        double offX = (ActualWidth - (maxX - minX) * scale) / 2, offY = (Height - (maxY - minY) * scale) / 2;
        var fg = (Brush)FindResource("Fg");
        var muted = (Brush)FindResource("Muted");
        var accent = (Brush)FindResource("Accent");
        var panel = (Brush)FindResource("Panel3");
        foreach (var m in _monitors)
        {
            bool sel = string.Equals(m.DeviceName, _selected, StringComparison.OrdinalIgnoreCase);
            var b = new Border
            {
                Width = Math.Max(10, m.Width * scale - 3),
                Height = Math.Max(8, m.Height * scale - 3),
                Background = sel ? accent : panel,
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                ToolTip = m.Label,
                Child = new TextBlock
                {
                    Text = m.ShortName.Replace("DISPLAY", ""),
                    Foreground = sel ? Brushes.Black : muted,
                    FontSize = 11,
                    FontWeight = sel ? FontWeights.Bold : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var mon = m;
            b.MouseLeftButtonUp += (_, _) => { _selected = mon.DeviceName; Rebuild(); Selected?.Invoke(mon); };
            SetLeft(b, offX + (m.Left - minX) * scale + 1.5);
            SetTop(b, offY + (m.Top - minY) * scale + 1.5);
            Children.Add(b);
        }
    }
}
