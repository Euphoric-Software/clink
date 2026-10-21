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
/// Clip trim editor: a preview that plays one audio track at a time (the one marked Listen, at its export volume, so it
/// sounds like the saved file does in a player), per-track include/volume for the export, an editor-style timeline with
/// in/out handles, and export via <see cref="Trimmer"/> (dropped/attenuated tracks are applied to the file).
/// </summary>
public partial class TrimWindow : Window
{
    private const int PreviewMaxWidth = 1280;
    private const double FallbackFrameStep = 1.0 / 60;

    private readonly ClipEntry _entry;
    private readonly DispatcherTimer _tick;
    private readonly List<TrackRow> _rows = new();
    private ClipPlayer? _player;
    private WriteableBitmap? _bitmap;
    private double _duration;
    private bool _playing, _busy, _closed, _audioWarned, _playerReleased;
    private double _dragSeek = double.NaN;
    private double _shownPts = double.NaN;
    private int _monitor = -1;

    private sealed class TrackRow
    {
        public CheckBox Include = null!;
        public RadioButton Listen = null!;
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
        var player = await CreatePlayerAsync();
        if (_closed) { player?.Dispose(); return; }
        if (player != null)
        {
            AttachPlayer(player);
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

    private async Task<ClipPlayer?> CreatePlayerAsync()
    {
        string path = _entry.Path;
        StatusText.Text = "Opening preview…";
        try
        {
            return await Task.Run(() => new ClipPlayer(path, PreviewMaxWidth));
        }
        catch (Exception ex)
        {
            Log.Warn("Trim preview failed: " + ex.Message);
            StatusText.Text = "Preview unavailable: " + ex.Message;
            TracksHint.Text = "No preview; the trim still works.";
            return null;
        }
    }

    private void AttachPlayer(ClipPlayer player)
    {
        _player = player;
        _playerReleased = false;
        _shownPts = double.NaN;
        if (player.Duration > 0.05) _duration = player.Duration;
        _bitmap = new WriteableBitmap(player.VideoWidth, player.VideoHeight, 96, 96, PixelFormats.Bgr32, null);
        PreviewImage.Source = _bitmap;
        player.Ended += () => Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _player != player) return;
            Pause();
            Seek(Timeline.SelectionStart);
        });
    }

    /// <summary>Brings the preview back after the player had to be closed (to replace the original) and the save then failed.</summary>
    private async Task ReopenPreviewAsync()
    {
        string status = StatusText.Text;
        double pos = Timeline.Position;
        var player = await CreatePlayerAsync();
        if (_closed) { player?.Dispose(); return; }
        if (player == null) return;
        AttachPlayer(player);
        if (player.TrackCount == _rows.Count) SyncPlayerMix(); else BuildTrackRows(player);
        StatusText.Text = status;
        _tick.Start();
        Seek(pos);
    }

    private void BuildTrackRows(ClipPlayer player)
    {
        TracksPanel.Children.Clear();
        _rows.Clear();
        _monitor = -1;
        if (player.TrackCount == 0)
        {
            TracksHint.Text = "";
            TracksPanel.Children.Add(new TextBlock { Text = "This clip has no audio.", Style = (Style)FindResource("MutedText") });
            return;
        }
        TracksHint.Text = "Ticked tracks are saved, each at its volume · Listen picks the one the preview plays (players play one track, usually the first)";
        string[]? names = _entry.AudioTracks != null && _entry.AudioTracks.Length == player.TrackCount ? _entry.AudioTracks : null;
        for (int i = 0; i < player.TrackCount; i++)
        {
            int index = i;
            string name = names != null && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"Audio {i + 1}";
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var include = new CheckBox
            {
                IsChecked = true, Content = name, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Ticked: saved in the trimmed clip at the volume on the right. Unticked: left out of the file",
            };
            include.Checked += (_, _) => OnIncludeChanged(index);
            include.Unchecked += (_, _) => OnIncludeChanged(index);
            grid.Children.Add(include);

            var listen = new RadioButton
            {
                Content = "Listen", GroupName = "TrimMonitor", Style = (Style)FindResource("MonitorRadio"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                ToolTip = "Play this track in the preview. Players play one track at a time (usually the first), so the preview does too",
            };
            listen.Checked += (_, _) => { _monitor = index; SyncPlayerMix(); };
            Grid.SetColumn(listen, 1);
            grid.Children.Add(listen);

            var volume = new Slider
            {
                Minimum = 0, Maximum = 200, Value = 100, Width = 170, SmallChange = 5, LargeChange = 25,
                IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
                ToolTip = "Volume in the saved clip (right-click resets to 100%)",
            };
            var percent = new TextBlock { Text = "100%", FontFamily = new FontFamily("Consolas"), Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            volume.ValueChanged += (_, e) =>
            {
                percent.Text = $"{e.NewValue:0}%";
                SyncPlayerMix();
            };
            volume.MouseRightButtonUp += (_, _) => volume.Value = 100;
            Grid.SetColumn(volume, 2);
            Grid.SetColumn(percent, 3);
            grid.Children.Add(volume);
            grid.Children.Add(percent);
            TracksPanel.Children.Add(grid);
            _rows.Add(new TrackRow { Include = include, Listen = listen, Volume = volume });
        }
        _rows[0].Listen.IsChecked = true; // what a player plays by default
        SyncPlayerMix();
    }

    /// <summary>
    /// Pushes the rows into the player: only the Listen track is audible, at its export volume, so the preview is the
    /// same signal a player gets from the saved file instead of a sum of every track (game audio twice, clipping).
    /// </summary>
    private void SyncPlayerMix()
    {
        var player = _player;
        if (player == null) return;
        for (int i = 0; i < _rows.Count && i < player.TrackCount; i++)
        {
            player.Gain[i] = _rows[i].Gain;
            player.Muted[i] = i != _monitor || !_rows[i].Included;
        }
    }

    private void OnIncludeChanged(int index)
    {
        if (index >= _rows.Count) return;
        var row = _rows[index];
        row.Listen.IsEnabled = row.Included;
        if (!row.Included && _monitor == index)
        {
            // Listen follows the export: audition the first track that is still in the file.
            int next = _rows.FindIndex(r => r.Included);
            if (next >= 0) _rows[next].Listen.IsChecked = true;
        }
        else if (row.Included && (_monitor < 0 || _monitor >= _rows.Count || !_rows[_monitor].Included))
        {
            row.Listen.IsChecked = true;
        }
        SyncPlayerMix();
    }

    // ------------------------------------------------------------------ playback

    private void Tick()
    {
        var player = _player;
        if (player == null) return;
        if (player.LastError != null && _playing) { Pause(); StatusText.Text = "Preview stopped: " + player.LastError; }
        if (player.AudioError != null && !_audioWarned && !_busy) { _audioWarned = true; StatusText.Text = "Preview audio: " + player.AudioError; }
        double pos = player.Position;
        if (_bitmap != null && player.TryGetFrame(pos, out byte[] bgra, out double pts))
        {
            int w = _bitmap.PixelWidth, h = _bitmap.PixelHeight;
            if (bgra.Length >= w * h * 4) _bitmap.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
            _shownPts = pts;
        }
        // Stop at the out point only while really playing: a scrub or handle drag past it must not yank the playhead back.
        if (_playing && !Timeline.IsScrubbing && !Timeline.IsDraggingHandle && pos >= Timeline.SelectionEnd - 0.001)
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

    /// <summary>m:ss.t, rounded to a tenth first so 59.97 reads 1:00.0 and not 0:60.0.</summary>
    private static string Fmt(double s)
    {
        long tenths = (long)Math.Round(Math.Max(0, s) * 10, MidpointRounding.AwayFromZero);
        return $"{tenths / 600}:{(tenths % 600) / 10.0:00.0}";
    }

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

    /// <summary>Moves by whole frames of the clip's own frame rate, from the frame on screen when the clock sits inside it.</summary>
    private void Step(int frames)
    {
        Pause();
        double dur = _player?.FrameDuration ?? FallbackFrameStep;
        double from = CurrentPosition;
        if (!double.IsNaN(_shownPts) && Math.Abs(_shownPts - from) < dur) from = _shownPts;
        Seek(from + frames * dur);
    }

    private void SetIn()
    {
        double v = Timeline.SnapToKeyframe(CurrentPosition);
        Timeline.SelectionStart = Math.Clamp(v, 0, Math.Max(0, Timeline.SelectionEnd - TimelineControl.MinSelection));
        UpdateTexts(CurrentPosition);
    }

    private void SetOut()
    {
        // Pause first: playback would otherwise hit the new out point on the next tick and jump back to the in point.
        if (_playing) Pause();
        double v = Math.Clamp(CurrentPosition, Math.Min(_duration, Timeline.SelectionStart + TimelineControl.MinSelection), _duration);
        Timeline.SelectionEnd = v;
        UpdateTexts(CurrentPosition);
    }

    private void OnScrub(double t)
    {
        if (_playing) Pause(); // scrubbing takes over the playhead, like in any editor
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
            // Toggles must not auto-repeat while the key is held; frame stepping may.
            case Key.Space: if (!e.IsRepeat) TogglePlay(); break;
            case Key.I: if (!e.IsRepeat) SetIn(); break;
            case Key.O: if (!e.IsRepeat) SetOut(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
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
        var app = AppController.Instance;
        // Trims are written to the local clips folder (docs/SPEC-v2.md §9: local first, the archive service moves them later
        // under the normal rules). A source that was archived, or that lives elsewhere, is not a reason to write next to it:
        // a trim written onto the share would be a plain local entry with a UNC path, checked with File.Exists on the UI thread.
        string srcDir = Path.GetDirectoryName(src) ?? app.Settings.ClipsFolder;
        string dir = _entry.IsArchived || !ArchiveService.IsSameFolder(srcDir, app.Settings.ClipsFolder) ? app.Settings.ClipsFolder : srcDir;
        string stem = Path.GetFileNameWithoutExtension(src);
        string dst = Path.Combine(dir, stem + " (trim).mp4");
        int n = 2;
        while (File.Exists(dst)) dst = Path.Combine(dir, stem + $" (trim {n++}).mp4");
        bool trimmed = false; // once true the output is complete and is never deleted, whatever happens after
        try
        {
            if (end - start < 0.1) throw new InvalidOperationException("Selection is too short");
            Directory.CreateDirectory(dir);
            string[]? names = _entry.AudioTracks;
            TrackMix[]? mix = _rows.Count > 0 ? _rows.Select(r => new TrackMix(r.Included, r.Gain)).ToArray() : null;
            if (mix != null && !mix.Any(m => m.Include)) StatusText.Text = "Trimming (no audio)…";
            await Task.Run(() => Trimmer.Trim(src, dst, start, end, names, mix));
            trimmed = true;
            string[] keptNames = KeptTrackNames(names, mix);
            ClipEntry result;
            string? replaceProblem = replace ? await ReplaceOriginalAsync(src, dst) : null;
            if (replace && replaceProblem == null)
            {
                _entry.Duration = end - start;
                _entry.Size = new FileInfo(src).Length;
                _entry.AudioTracks = keptNames;
                _entry.CopiumLink = null; _entry.CopiumPageUrl = null; _entry.CopiumId = null;
                app.Library.Update(_entry);
                result = _entry;
                StatusText.Text = "Replaced " + Path.GetFileName(src);
            }
            else
            {
                result = app.Library.Add(dst, end - start, new FileInfo(dst).Length, _entry.Game, keptNames, isTrim: true, sourcePath: src);
                StatusText.Text = replaceProblem != null
                    ? $"Could not replace the original ({replaceProblem}); saved as {Path.GetFileName(dst)} instead"
                    : "Saved " + Path.GetFileName(dst);
            }
            if (share)
            {
                StatusText.Text = "Uploading…";
                var progress = new Progress<double>(p => StatusText.Text = $"Uploading… {p:P0}");
                bool ok = await app.ShareAsync(result, progress);
                StatusText.Text = ok ? "Uploaded, link copied" : "Upload failed";
            }
            await Task.Delay(replaceProblem != null ? 3000 : 1200);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Trim", ex);
            if (!trimmed)
            {
                StatusText.Text = "Trim failed: " + ex.Message;
                try { if (File.Exists(dst) && !string.Equals(dst, src, StringComparison.OrdinalIgnoreCase)) File.Delete(dst); } catch { }
            }
            else
            {
                // The trim itself finished: keep the file and say where it is.
                string kept = File.Exists(dst) ? dst : src;
                StatusText.Text = $"Saved {Path.GetFileName(kept)}, but: {ex.Message}";
            }
            SaveButton.IsEnabled = SaveShareButton.IsEnabled = true;
            if (_playerReleased && !_closed) await ReopenPreviewAsync();
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// Puts the finished trim in place of the original with one rename-over, so no step can leave the user with neither
    /// file: the original is only gone once the new file has its name. Returns null on success, else a short reason;
    /// the trimmed file is untouched either way.
    /// </summary>
    private async Task<string?> ReplaceOriginalAsync(string src, string dst)
    {
        // Our own readers hold the original open; release them (and wait for that to really happen) before renaming.
        _tick.Stop();
        var p = _player;
        _player = null;
        PreviewImage.Source = null;
        _playerReleased = p != null;
        bool released = true;
        try { released = p?.Close() ?? true; }
        catch (Exception ex) { Log.Warn("Preview close: " + ex.Message); released = false; }
        if (!released) return "the preview is still reading it";
        await Task.Delay(200);
        try
        {
            await Task.Run(() => File.Move(dst, src, overwrite: true));
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("Trim: could not replace the original: " + ex.Message);
            const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);
            return ex.HResult is SharingViolation or LockViolation ? "file in use" : ex.Message;
        }
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
