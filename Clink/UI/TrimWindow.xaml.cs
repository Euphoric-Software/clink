using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clink.Engine;
using Clink.Playback;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// Clip trim editor: preview with every audio track mixed (per-track include/volume), an editor-style timeline
/// with in/out handles, and export via <see cref="Trimmer"/> (dropped/attenuated tracks are applied to the file).
/// </summary>
public partial class TrimWindow : Window
{
    private const int PreviewMaxWidth = 1280;
    private const double FrameStep = 1.0 / 60;

    private readonly ClipEntry _entry;
    private readonly DispatcherTimer _tick;
    private readonly List<TrackRow> _rows = new();
    private ClipPlayer? _player;
    private WriteableBitmap? _bitmap;
    private double _duration;
    private bool _playing, _busy, _closed;
    private double _dragSeek = double.NaN;

    private sealed class TrackRow
    {
        public CheckBox Include = null!;
        public Slider Volume = null!;
        public bool Included => Include.IsChecked == true;
        public float Gain => (float)(Volume.Value / 100.0);
    }

    internal TrimWindow(ClipEntry entry)
    {
        _entry = entry;
        InitializeComponent();
        TitleText.Text = "Trim · " + Path.GetFileName(entry.Path);
        _duration = Math.Max(TimelineControl.MinSelection, entry.Duration);
        Timeline.Duration = _duration;
        Timeline.SelectionStart = 0;
        Timeline.SelectionEnd = _duration;
        Timeline.PositionSeeked += OnScrub;
        Timeline.SelectionChanged += OnSelectionChanged;
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _tick.Tick += (_, _) => Tick();
        UpdateTexts(0);
        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _tick.Stop();
            var p = _player;
            _player = null;
            try { p?.Dispose(); } catch (Exception ex) { Log.Warn("Preview dispose: " + ex.Message); }
        };
    }

    // ------------------------------------------------------------------ setup

    private async Task OpenAsync()
    {
        string path = _entry.Path;
        StatusText.Text = "Opening preview…";
        ClipPlayer? player = null;
        try
        {
            player = await Task.Run(() => new ClipPlayer(path, PreviewMaxWidth));
        }
        catch (Exception ex)
        {
            Log.Warn("Trim preview failed: " + ex.Message);
            StatusText.Text = "Preview unavailable: " + ex.Message;
            TracksHint.Text = "No preview; the trim still works.";
        }
        if (_closed) { player?.Dispose(); return; }
        if (player != null)
        {
            _player = player;
            if (player.Duration > 0.05) _duration = player.Duration;
            _bitmap = new WriteableBitmap(player.VideoWidth, player.VideoHeight, 96, 96, PixelFormats.Bgr32, null);
            PreviewImage.Source = _bitmap;
            player.Ended += () => Dispatcher.BeginInvoke(() =>
            {
                if (_closed || _player != player) return;
                Pause();
                Seek(Timeline.SelectionStart);
            });
            BuildTrackRows(player);
            StatusText.Text = "";
        }
        Timeline.Duration = _duration;
        Timeline.SelectionStart = 0;
        Timeline.SelectionEnd = _duration;
        UpdateTexts(0);
        _tick.Start();

        _ = Task.Run(() =>
        {
            try
            {
                var keys = Trimmer.GetKeyframeTimes(path);
                Dispatcher.BeginInvoke(() => { if (!_closed) Timeline.Keyframes = keys; });
            }
            catch (Exception ex) { Log.Warn("keyframe scan: " + ex.Message); }
        });
    }

    private void BuildTrackRows(ClipPlayer player)
    {
        TracksPanel.Children.Clear();
        _rows.Clear();
        if (player.TrackCount == 0)
        {
            TracksPanel.Children.Add(new TextBlock { Text = "This clip has no audio.", Style = (Style)FindResource("MutedText") });
            return;
        }
        string[]? names = _entry.AudioTracks != null && _entry.AudioTracks.Length == player.TrackCount ? _entry.AudioTracks : null;
        for (int i = 0; i < player.TrackCount; i++)
        {
            int index = i;
            string name = names != null && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"Audio {i + 1}";
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var include = new CheckBox { IsChecked = true, Content = name, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Unchecked: muted in the preview and left out of the saved clip" };
            include.Checked += (_, _) => { player.Muted[index] = false; };
            include.Unchecked += (_, _) => { player.Muted[index] = true; };
            grid.Children.Add(include);

            var volume = new Slider
            {
                Minimum = 0, Maximum = 200, Value = 100, Width = 170, SmallChange = 5, LargeChange = 25,
                IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
                ToolTip = "Volume (right-click resets to 100%)",
            };
            var percent = new TextBlock { Text = "100%", FontFamily = new FontFamily("Consolas"), Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            volume.ValueChanged += (_, e) =>
            {
                player.Gain[index] = (float)(e.NewValue / 100.0);
                percent.Text = $"{e.NewValue:0}%";
            };
            volume.MouseRightButtonUp += (_, _) => volume.Value = 100;
            Grid.SetColumn(volume, 1);
            Grid.SetColumn(percent, 2);
            grid.Children.Add(volume);
            grid.Children.Add(percent);
            TracksPanel.Children.Add(grid);
            _rows.Add(new TrackRow { Include = include, Volume = volume });
        }
    }

    // ------------------------------------------------------------------ playback

    private void Tick()
    {
        var player = _player;
        if (player == null) return;
        double pos = player.Position;
        if (_bitmap != null && player.TryGetFrame(pos, out byte[] bgra, out _))
        {
            int w = _bitmap.PixelWidth, h = _bitmap.PixelHeight;
            if (bgra.Length >= w * h * 4) _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
        }
        if (_playing && pos >= Timeline.SelectionEnd - 0.001)
        {
            Pause();
            Seek(Timeline.SelectionStart);
            pos = Timeline.SelectionStart;
        }
        if (!double.IsNaN(_dragSeek))
        {
            player.Seek(_dragSeek); // throttled to the tick rate; the player coalesces anyway
            pos = _dragSeek;
            _dragSeek = double.NaN;
        }
        if (!Timeline.IsScrubbing && Timeline.Position != pos) Timeline.Position = pos;
        UpdateTexts(pos);
    }

    private double CurrentPosition => _player?.Position ?? Timeline.Position;

    private void UpdateTexts(double pos)
    {
        double len = Math.Max(0, Timeline.SelectionEnd - Timeline.SelectionStart);
        PosText.Text = $"{Fmt(pos)} / {Fmt(_duration)}";
        PosText.ToolTip = $"Selection {Fmt(Timeline.SelectionStart)} → {Fmt(Timeline.SelectionEnd)} ({len:0.0}s)";
    }

    private static string Fmt(double s) => $"{(int)(s / 60)}:{s % 60:00.0}";

    private void Play()
    {
        if (_player == null || _busy) return;
        double pos = _player.Position;
        if (pos >= Timeline.SelectionEnd - 0.05) Seek(Timeline.SelectionStart);
        _player.Play();
        _playing = true;
        PlayButton.Content = "❚❚";
    }

    private void Pause()
    {
        _player?.Pause();
        _playing = false;
        PlayButton.Content = "▶";
    }

    private void TogglePlay() { if (_playing) Pause(); else Play(); }

    private void Seek(double t)
    {
        t = Math.Clamp(t, 0, _duration);
        _player?.Seek(t);
        Timeline.Position = t;
        UpdateTexts(t);
    }

    private void Step(double delta)
    {
        Pause();
        Seek(CurrentPosition + delta);
    }

    private void SetIn()
    {
        double v = Timeline.SnapToKeyframe(CurrentPosition);
        Timeline.SelectionStart = Math.Clamp(v, 0, Math.Max(0, Timeline.SelectionEnd - TimelineControl.MinSelection));
        UpdateTexts(CurrentPosition);
    }

    private void SetOut()
    {
        double v = Math.Clamp(CurrentPosition, Math.Min(_duration, Timeline.SelectionStart + TimelineControl.MinSelection), _duration);
        Timeline.SelectionEnd = v;
        UpdateTexts(CurrentPosition);
    }

    private void OnScrub(double t)
    {
        _player?.Seek(t);
        UpdateTexts(t);
    }

    private void OnSelectionChanged()
    {
        if (Timeline.IsDraggingHandle)
        {
            // Show the frame under the handle while it is being dragged.
            if (_playing) Pause();
            _dragSeek = Timeline.DraggingHandle == 1 ? Timeline.SelectionStart : Timeline.SelectionEnd;
        }
        UpdateTexts(CurrentPosition);
    }

    // ------------------------------------------------------------------ input

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void Preview_Click(object sender, MouseButtonEventArgs e) => TogglePlay();
    private void SetIn_Click(object sender, RoutedEventArgs e) => SetIn();
    private void SetOut_Click(object sender, RoutedEventArgs e) => SetOut();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy) return;
        if (Keyboard.FocusedElement is Slider && e.Key is Key.Left or Key.Right or Key.Home or Key.End) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.I: SetIn(); break;
            case Key.O: SetOut(); break;
            case Key.Left: Step(-FrameStep); break;
            case Key.Right: Step(FrameStep); break;
            case Key.Home: Pause(); Seek(Timeline.SelectionStart); break;
            case Key.End: Pause(); Seek(Timeline.SelectionEnd); break;
            default: return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ save

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(share: false);
    private async void SaveShare_Click(object sender, RoutedEventArgs e) => await SaveAsync(share: true);

    private async Task SaveAsync(bool share)
    {
        if (_busy) return;
        _busy = true;
        Pause();
        SaveButton.IsEnabled = SaveShareButton.IsEnabled = false;
        StatusText.Text = "Trimming…";
        double start = Timeline.SnapToKeyframe(Timeline.SelectionStart), end = Timeline.SelectionEnd;
        bool replace = ReplaceCheck.IsChecked == true;
        string src = _entry.Path;
        string dst = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + " (trim).mp4");
        int n = 2;
        while (File.Exists(dst)) dst = Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + $" (trim {n++}).mp4");
        try
        {
            if (end - start < 0.1) throw new InvalidOperationException("Selection is too short");
            string[]? names = _entry.AudioTracks;
            TrackMix[]? mix = _rows.Count > 0 ? _rows.Select(r => new TrackMix(r.Included, r.Gain)).ToArray() : null;
            if (mix != null && !mix.Any(m => m.Include)) StatusText.Text = "Trimming (no audio)…";
            await Task.Run(() => Trimmer.Trim(src, dst, start, end, names, mix));
            string[] keptNames = KeptTrackNames(names, mix);
            var app = AppController.Instance;
            ClipEntry result;
            if (replace)
            {
                _tick.Stop();
                var p = _player;
                _player = null;
                PreviewImage.Source = null;
                try { p?.Dispose(); } catch { }
                await Task.Delay(200);
                File.Delete(src);
                File.Move(dst, src);
                _entry.Duration = end - start;
                _entry.Size = new FileInfo(src).Length;
                _entry.AudioTracks = keptNames;
                _entry.CopiumLink = null; _entry.CopiumPageUrl = null; _entry.CopiumId = null;
                app.Library.Update(_entry);
                result = _entry;
            }
            else
            {
                result = app.Library.Add(dst, end - start, new FileInfo(dst).Length, _entry.Game, keptNames);
            }
            StatusText.Text = "Saved " + Path.GetFileName(result.Path);
            if (share)
            {
                StatusText.Text = "Uploading…";
                var progress = new Progress<double>(p => StatusText.Text = $"Uploading… {p:P0}");
                bool ok = await app.ShareAsync(result, progress);
                StatusText.Text = ok ? "Uploaded, link copied" : "Upload failed";
            }
            await Task.Delay(1200);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Trim", ex);
            StatusText.Text = "Trim failed: " + ex.Message;
            SaveButton.IsEnabled = SaveShareButton.IsEnabled = true;
            try { if (File.Exists(dst) && !string.Equals(dst, src, StringComparison.OrdinalIgnoreCase)) File.Delete(dst); } catch { }
        }
        finally { _busy = false; }
    }

    /// <summary>Names of the tracks that end up in the exported file (dropped ones removed), in file order.</summary>
    private static string[] KeptTrackNames(string[]? names, TrackMix[]? mix)
    {
        if (mix == null) return names ?? Array.Empty<string>();
        var kept = new List<string>();
        for (int i = 0; i < mix.Length; i++)
        {
            if (!mix[i].Include) continue;
            kept.Add(names != null && i < names.Length ? names[i] : $"Audio {i + 1}");
        }
        return kept.ToArray();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
