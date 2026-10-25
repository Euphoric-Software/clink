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
/// The clip window, in two modes. The <b>player</b> (what a click on a clip opens): the preview, play / pause, the position, a
/// plain seek bar and the audio rows, where each track has a Listen pill and a volume slider; Game, Mic and Voice also change
/// "All audio" live (<see cref="MixPlan"/>). The <b>trim editor</b> (the "Trim" switch next to Play): the same plus an
/// editor-style timeline with in/out handles, include boxes for the tracks, and the save row. A preview plays one audio track
/// at a time (the one marked Listen, at its export volume, so it sounds like the saved file does in a player), and the export
/// goes through <see cref="Trimmer"/> (dropped/attenuated tracks are applied to the file).
/// </summary>
public partial class TrimWindow : Window
{
    private const int PreviewMaxWidth = 1280;
    private const double FallbackFrameStep = 1.0 / 60;

    private static readonly List<TrimWindow> s_open = new();

    private readonly ClipEntry _entry;
    private readonly DispatcherTimer _tick;
    private readonly List<TrackRow> _rows = new();
    private ClipPlayer? _player;
    private WriteableBitmap? _bitmap;
    private double _duration;
    private bool _playing, _closed, _audioWarned, _playerReleased;
    private bool _busyNow;
    /// <summary>A save is running: the "Trim" switch is off meanwhile, so the save row cannot disappear under it.</summary>
    private bool _busy { get => _busyNow; set { _busyNow = value; TrimToggle.IsEnabled = !value; } }
    private double _dragSeek = double.NaN;
    private double _shownPts = double.NaN;
    private int _monitor = -1;
    private int _openGen; // bumped per OpenAsync so a keyframe scan of an earlier version of the file is ignored
    private int _keyScanGen = -1; // the _openGen the keyframe scan was started for
    private bool _trimMode;       // the trim editor (true) or the player (false)
    private bool _settingMode;    // SetTrimMode is moving the switch: its Checked / Unchecked are not a click
    private TrackRole[] _roles = Array.Empty<TrackRole>();   // what each row's track is (Mix, Game…), from the names in the file
    private TrackRole[] _unlined = Array.Empty<TrackRole>(); // sources of an older clip that could not be lined up with the Mix: _roles has them standing alone
    private bool _untrusted;                                  // ... because this Windows failed the timeline self-check (ClipSource), not because of the clip
    private long _openedAt;       // Environment.TickCount64 when the window first showed or was brought forward

    private sealed class TrackRow
    {
        public string Name = "";    // the track's name in the file: the export keeps it
        public string Label = "";   // what the row shows (the Mix is "All audio (mix)")
        public CheckBox Include = null!;
        public TextBlock NameText = null!;   // the name where the player has no Include box
        public RadioButton Listen = null!;
        public Slider Volume = null!;
        public TextBlock Percent = null!;
        public bool Included => Include.IsChecked == true;
        public float Gain => (float)(Volume.Value / 100.0);
    }

    internal TrimWindow(ClipEntry entry, bool trimMode = true)
    {
        _entry = entry;
        InitializeComponent();
        AppIcons.Attach(this);   // has its own taskbar button when it has no owner
        _duration = Math.Max(TimelineControl.MinSelection, entry.Duration);
        Timeline.Duration = _duration;
        Timeline.SelectionStart = 0;
        Timeline.SelectionEnd = _duration;
        Timeline.PositionSeeked += OnScrub;
        Timeline.SelectionChanged += OnSelectionChanged;
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _tick.Tick += (_, _) => Tick();
        SetTrimMode(trimMode);
        UpdateTexts(0);
        // Cloud sharing off (Settings → App): no "Save & share"; switched while the editor is open, it follows.
        void ShowShareButton() => SaveShareButton.Visibility = AppController.Instance?.Settings.CloudSharingEnabled == false ? Visibility.Collapsed : Visibility.Visible;
        ShowShareButton();
        if (AppController.Instance is { } cloudApp)
        {
            cloudApp.CloudSharingChanged += ShowShareButton;
            Closed += (_, _) => cloudApp.CloudSharingChanged -= ShowShareButton;
        }
        s_open.Add(this);
        _openedAt = Environment.TickCount64;
        ContentRendered += (_, _) => _openedAt = Environment.TickCount64;
        // The window comes up under the pointer of the click that opened it, and a double click would land its second click in
        // here (the video, a button): clicks in the first moments are the opening click's, not a choice.
        PreviewMouseDown += (_, e) => { if (Environment.TickCount64 - _openedAt < 350) e.Handled = true; };
        Loaded += async (_, _) => await OpenAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            s_open.Remove(this);
            _tick.Stop();
            var p = _player;
            _player = null;
            try { p?.Dispose(); } catch (Exception ex) { Log.Warn("Preview dispose: " + ex.Message); }
        };
    }

    /// <summary>The open window for this clip, if there is one (a second click on its row must not stack another).</summary>
    internal static TrimWindow? Find(ClipEntry entry)
        => s_open.FirstOrDefault(w => ReferenceEquals(w._entry, entry) || string.Equals(w._entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Brings the window forward in the mode asked for (a save in progress keeps its mode).</summary>
    internal void Reveal(bool trim)
    {
        // A second click on the clip's row (or on its Trim button) lands here and brings this window up under the pointer: like the
        // opening click's second half it must not reach a button of the window (a double click on the row would pause the preview).
        _openedAt = Environment.TickCount64;
        if (!_busy && trim != _trimMode) SetTrimMode(trim);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------------ setup

    /// <summary>
    /// Opens the preview and sets the editor up for the whole clip with fresh track rows. Also used when the original was
    /// just replaced by its trim and the window stays open: the old timeline, keyframes and volumes described the old file.
    /// </summary>
    private async Task OpenAsync(bool keepStatus = false)
    {
        string status = StatusText.Text;
        ++_openGen;
        _duration = Math.Max(TimelineControl.MinSelection, _entry.Duration);
        Timeline.Keyframes = null;
        var player = await CreatePlayerAsync();
        if (_closed) { player?.Dispose(); return; }
        if (player != null)
        {
            AttachPlayer(player);
            BuildTrackRows(player);
            StatusText.Text = "";
        }
        else
        {
            TracksPanel.Children.Clear();
            _rows.Clear();
            _roles = Array.Empty<TrackRole>();
            _unlined = Array.Empty<TrackRole>();
            _untrusted = false;
            MixNote.Visibility = Visibility.Collapsed;
            _monitor = -1;
        }
        if (keepStatus) StatusText.Text = status;
        Timeline.Duration = _duration;
        Timeline.SelectionStart = 0;
        Timeline.SelectionEnd = _duration;
        Timeline.Position = 0;
        UpdateTexts(0);
        _tick.Start();
        if (_trimMode) StartKeyframeScan();   // only the trim editor snaps to keyframes: a clip that is just played is not read through for them
    }

    /// <summary>Finds the keyframes of this version of the file in the background (once per version); the timeline draws them and the start snaps to them.</summary>
    private void StartKeyframeScan()
    {
        if (_keyScanGen == _openGen) return;
        _keyScanGen = _openGen;
        int gen = _openGen;
        string path = _entry.Path;
        _ = Task.Run(() =>
        {
            try
            {
                var keys = Trimmer.GetKeyframeTimes(path);
                Dispatcher.BeginInvoke(() => { if (!_closed && gen == _openGen) Timeline.Keyframes = keys; });
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
            Seek(RestartAt);
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
        _roles = Array.Empty<TrackRole>();
        _unlined = Array.Empty<TrackRole>();
        _untrusted = false;
        MixNote.Visibility = Visibility.Collapsed;
        if (player.TrackCount == 0)
        {
            TracksHint.Text = "";
            TracksPanel.Children.Add(new TextBlock { Text = "This clip has no audio.", Style = (Style)FindResource("MutedText") });
            return;
        }
        // The file's own names first: the library's list can also name tracks that were not recorded (mix-only sources).
        string[]? names = player.TrackNames
            ?? (_entry.AudioTracks != null && _entry.AudioTracks.Length == player.TrackCount ? _entry.AudioTracks : null);
        // What each track is comes from the names in the file itself (a file Clink did not write has none): only a Clink clip's
        // first track is a Mix that its Game, Mic and Voice tracks can be mixed into again. A source of a clip an older Clink
        // trimmed, which could not be lined up with the Mix (the player checked), stands alone: its slider is for its own track.
        var fileRoles = MixPlan.Roles(player.TrackNames, player.TrackCount);
        var unlined = player.Alignment.Unlined;
        _roles = MixPlan.WithoutTracks(fileRoles, unlined);
        _unlined = unlined.Where(i => i > 0 && i < fileRoles.Length && _roles[i] != fileRoles[i]).Select(i => fileRoles[i]).ToArray();
        _untrusted = player.Alignment.Untrusted;
        bool remix = MixPlan.CanRemix(_roles);
        for (int i = 0; i < player.TrackCount; i++)
        {
            int index = i;
            string name = names != null && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"Audio {i + 1}";
            string label = _roles[i] == TrackRole.Mix ? "All audio (mix)" : name;   // the file keeps the name "Mix"
            bool feeds = MixPlan.FeedsMix(_roles, i);
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var include = new CheckBox
            {
                IsChecked = true, Content = label, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = feeds ? $"Ticked: {name} is also saved as a track of its own, at the volume on the right. Unticked: no track of its own, but its volume still shapes All audio"
                    : "Ticked: saved in the trimmed clip at the volume on the right. Unticked: left out of the file",
            };
            include.Checked += (_, _) => OnIncludeChanged(index);
            include.Unchecked += (_, _) => OnIncludeChanged(index);
            grid.Children.Add(include);
            // The player has no include boxes: the name stands in the same place (23 px = the box and its gap).
            var nameText = new TextBlock
            {
                Text = label, Margin = new Thickness(23, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            grid.Children.Add(nameText);

            var listen = new RadioButton
            {
                Content = "Listen", GroupName = "TrimMonitor", Style = (Style)FindResource("MonitorRadio"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                ToolTip = _roles[i] == TrackRole.Mix && remix
                    ? "Play All audio in the preview, with the Game, Mic and Voice volumes below applied. Players play one track at a time, usually this one"
                    : "Play this track in the preview. Players play one track at a time (usually the first), so the preview does too",
            };
            listen.Checked += (_, _) => { _monitor = index; SyncPlayerMix(); };
            Grid.SetColumn(listen, 1);
            grid.Children.Add(listen);

            var volume = new Slider
            {
                Minimum = 0, Maximum = 200, Value = 100, Width = 170, SmallChange = 5, LargeChange = 25,
                IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
                ToolTip = feeds ? $"{name}'s volume, in its own track and inside All audio (right-click resets to 100%)"
                    : _roles[i] == TrackRole.Mix && remix ? "All audio's volume (right-click resets to 100%)"
                    : "Volume in the saved clip (right-click resets to 100%)",
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
            _rows.Add(new TrackRow { Name = name, Label = label, Include = include, NameText = nameText, Listen = listen, Volume = volume, Percent = percent });
        }
        RefreshRows();
        UpdateTracksHint();
        UpdateMixNote();
        _rows[0].Listen.IsChecked = true; // what a player plays by default
        SyncPlayerMix();
    }

    /// <summary>The hint over the track rows: what ticking, Listen and the sliders do in the current mode.</summary>
    private void UpdateTracksHint()
    {
        if (_rows.Count == 0) return;   // no audio, or no preview: the hint already says so
        // The sources this clip's All audio can be rebuilt from (Game, Mic, Voice: whichever of them the file has as tracks).
        var sources = MixPlan.CanRemix(_roles) ? new[] { TrackRole.Game, TrackRole.Mic, TrackRole.Voice }.Where(r => Array.IndexOf(_roles, r) >= 0).ToArray() : Array.Empty<TrackRole>();
        string names = JoinNames(sources, "and");
        TracksHint.Text = _trimMode
            ? sources.Length > 0 ? $"Ticked tracks are saved at their volume · Listen picks what you hear · {names} also shape{(sources.Length > 1 ? "" : "s")} All audio"
                                 : "Ticked tracks are saved, each at its volume · Listen picks the one you hear (players play one track, usually the first)"
            : sources.Length > 0 ? $"Listen picks what you hear · the {names} slider{(sources.Length > 1 ? "s change" : " changes")} All audio, live"
                                 : "Listen picks the track you hear (players play one track, usually the first) · the sliders set its volume";
        TracksHint.ToolTip = TracksHint.Text;
    }

    /// <summary>"Game", "Game and Mic", "Game, Mic and Voice".</summary>
    private static string JoinNames(IReadOnlyList<TrackRole> roles, string joiner)
        => roles.Count < 2 ? string.Join("", roles) : string.Join(", ", roles.Take(roles.Count - 1)) + $" {joiner} {roles[^1]}";

    /// <summary>
    /// The sources All audio has no track of its own to change (the default setting does not save Voice as a track): its volume
    /// cannot be changed, and taking the other sources out of the baked mix leaves a faint trace of them.
    /// </summary>
    private void UpdateMixNote()
    {
        // A source that could not be lined up is not "missing" (it has its track): it just does not feed All audio.
        var missing = MixPlan.MissingSources(_roles).Where(r => !_unlined.Contains(r)).ToArray();
        string? older = null;
        if (_unlined.Length > 0)
        {
            bool few = _unlined.Length == 1;
            older = $"{(_untrusted ? ClipAlignment.UntrustedNote : ClipAlignment.OlderClinkNote)}. The {JoinNames(_unlined, "and")} volume{(few ? "" : "s")} only change{(few ? "s" : "")} {(few ? "its" : "their")} own track{(few ? "" : "s")}.";
        }
        string? baked = null;
        if (missing.Length > 0)
        {
            var present = new[] { TrackRole.Game, TrackRole.Mic, TrackRole.Voice }.Where(r => !missing.Contains(r) && !_unlined.Contains(r)).ToArray();
            bool many = missing.Length > 1;
            baked = $"{JoinNames(missing, "and")} {(many ? "are" : "is")} only inside All audio (not saved as {(many ? "tracks" : "a track")} of {(many ? "their" : "its")} own), "
                + $"so {(many ? "their volumes" : "its volume")} cannot be changed. Turning {JoinNames(present, "or")} down takes it out of the baked mix again, which can leave a faint trace of it.";
        }
        if (older == null && baked == null)
        {
            MixNote.Visibility = Visibility.Collapsed;
            return;
        }
        MixNote.Text = older == null ? baked! : baked == null ? older : older + " " + baked;
        MixNote.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Pushes the rows into the player. Only the Listen track is audible, so the preview is the signal a player gets from the
    /// saved file and not a sum of every track (game audio twice, clipping); listening to All audio plays the sources at their
    /// sliders when it is rebuilt from them (<see cref="MixPlan.PreviewGains"/>), which is how the export renders it.
    /// </summary>
    private void SyncPlayerMix()
    {
        var player = _player;
        if (player == null) return;
        var include = _rows.Select(InFile).ToArray();
        var volume = _rows.Select(r => r.Gain).ToArray();
        var gains = MixPlan.PreviewGains(_roles, include, volume, _monitor);
        // A Mix put together from the sources is limited with the recording's soft knee, as the saved one is; a track on its own is not.
        player.SoftKnee = MixPlan.PreviewIsRemix(_roles, include, volume, _monitor);
        for (int i = 0; i < _rows.Count && i < player.TrackCount && i < gains.Length; i++)
        {
            player.Gain[i] = gains[i];
            player.Muted[i] = gains[i] == 0f;
        }
    }

    /// <summary>A row's track is in the preview: in the player every track is, in the trim editor the ticked ones (an unticked track is not in the saved file).</summary>
    private bool InFile(TrackRow row) => !_trimMode || row.Included;

    /// <summary>What each row offers in the current mode: the include box or the plain name, and what is greyed out.</summary>
    private void RefreshRows()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool on = InFile(row);
            // An unticked track is not in the file: its listen pill and volume grey out like the rest of the row's controls. A
            // source's slider also shapes All audio, so it stays usable when only its own track is left out.
            row.Listen.IsEnabled = on;
            row.Volume.IsEnabled = on || MixPlan.FeedsMix(_roles, i);
            row.Percent.Opacity = row.Volume.IsEnabled ? 1 : 0.45;
            row.Include.Visibility = _trimMode ? Visibility.Visible : Visibility.Collapsed;
            row.NameText.Visibility = _trimMode ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Listen follows the export: when the track being heard is not in the file, audition the first one that is.</summary>
    private void EnsureMonitor()
    {
        if (_rows.Count == 0 || (_monitor >= 0 && _monitor < _rows.Count && InFile(_rows[_monitor]))) return;
        int next = _rows.FindIndex(InFile);
        if (next >= 0) _rows[next].Listen.IsChecked = true;
    }

    private void OnIncludeChanged(int index)
    {
        if (index >= _rows.Count) return;
        RefreshRows();
        EnsureMonitor();
        SyncPlayerMix();
    }

    // ------------------------------------------------------------------ player / trim editor

    /// <summary>
    /// Shows the player (false) or the trim editor (true). Nothing is lost by switching: the selection, the volumes and the
    /// ticks stay as they are, the player simply ignores the selection and the ticks.
    /// </summary>
    private void SetTrimMode(bool trim)
    {
        _trimMode = trim;
        _settingMode = true;
        TrimToggle.IsChecked = trim;
        _settingMode = false;
        string name = Path.GetFileName(_entry.Path);
        TitleText.Text = trim ? "Trim · " + name : name;
        Title = TitleText.Text;
        Timeline.ShowSelection = trim;
        var editing = trim ? Visibility.Visible : Visibility.Collapsed;
        SetInButton.Visibility = SetOutButton.Visibility = SaveButtons.Visibility = editing;
        OpenExternalButton.Visibility = trim ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = trim ? "Start snaps to keyframes · Space play · I/O set in/out · ←/→ frame step" : "Space play / pause · ←/→ frame step · Home / End";
        // The bottom row stays in both modes because the status line is the player's too (a preview that failed says so there);
        // only the save buttons are the editor's, so it shrinks to that line.
        BottomBar.Padding = trim ? new Thickness(16, 10, 16, 14) : new Thickness(16, 2, 16, 8);
        BottomBar.BorderThickness = trim ? new Thickness(0, 1, 0, 0) : new Thickness(0);
        RefreshRows();
        EnsureMonitor();
        UpdateTracksHint();
        SyncPlayerMix();
        if (trim && _openGen > 0) StartKeyframeScan();
        UpdateTexts(CurrentPosition);
    }

    private void TrimToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingMode) SetTrimMode(TrimToggle.IsChecked == true);
    }

    /// <summary>Hands the file to the system's default video player (the clip list used to do this on a click).</summary>
    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        Pause();
        string path = _entry.Path;
        void Open()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => StatusText.Text = "Open failed: " + ex.Message); }
        }
        if (!_entry.IsArchived) { Open(); return; }
        // ShellExecute on a share path touches the share again: on its own STA thread (shell extensions expect one), not the UI's.
        var t = new Thread(Open) { IsBackground = true, Name = "Clink.OpenArchived" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
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
        // Stop at the out point (the end of the clip in the player) only while really playing: a scrub or handle drag past it must not yank the playhead back.
        if (_playing && !Timeline.IsScrubbing && !Timeline.IsDraggingHandle && pos >= StopAt - 0.001)
        {
            Pause();
            Seek(RestartAt);
            pos = RestartAt;
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

    /// <summary>Where playing stops: the out point in the trim editor, the end of the clip in the player.</summary>
    private double StopAt => _trimMode ? Timeline.SelectionEnd : _duration;

    /// <summary>Where the playhead goes back to when playing stops (and where Play starts again from): the in point, or the start of the clip.</summary>
    private double RestartAt => _trimMode ? Timeline.SelectionStart : 0;

    private void UpdateTexts(double pos)
    {
        double len = Math.Max(0, Timeline.SelectionEnd - Timeline.SelectionStart);
        PosText.Text = $"{Fmt(pos)} / {Fmt(_duration)}";
        PosText.ToolTip = _trimMode ? $"Selection {Fmt(Timeline.SelectionStart)} → {Fmt(Timeline.SelectionEnd)} ({len:0.0}s)" : null;
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
        if (pos >= StopAt - 0.05) Seek(RestartAt);
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
            case Key.I: if (!_trimMode) return; if (!e.IsRepeat) SetIn(); break;   // in / out points are the trim editor's
            case Key.O: if (!_trimMode) return; if (!e.IsRepeat) SetOut(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(1); break;
            case Key.Home: Pause(); Seek(RestartAt); break;
            case Key.End: Pause(); Seek(StopAt); break;
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
        bool replaced = false; // the original is now the trim: what the editor shows describes a file that is gone
        try
        {
            if (end - start < 0.1) throw new InvalidOperationException("Selection is too short");
            Directory.CreateDirectory(dir);
            // The rows' names (file order, as the mix); without rows the trimmer reads them from the file itself.
            string[]? names = _rows.Count > 0 ? _rows.Select(r => r.Name).ToArray() : null;
            // What the sliders did to All audio is rendered into it here (MixPlan), as the preview plays it.
            TrackMix[]? mix = _rows.Count > 0 ? MixPlan.Export(_roles, _rows.Select(r => r.Included).ToArray(), _rows.Select(r => r.Gain).ToArray()) : null;
            if (mix != null && !mix.Any(m => m.Include)) StatusText.Text = "Trimming (no audio)…";
            string[] keptNames = await Task.Run(() => Trimmer.Trim(src, dst, start, end, names, mix));
            trimmed = true;
            ClipEntry result;
            string? replaceProblem = replace ? await ReplaceOriginalAsync(src, dst) : null;
            if (replace && replaceProblem == null)
            {
                replaced = true;
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
                bool ok = await app.ShareAsync(result, progress, context: ConsentContext.TrimShare);
                if (!ok && app.LastShareOutcome == ShareOutcome.Declined)
                {
                    // "Not now" in the Share with Copium? pop-up, or cloud sharing off: the trim is saved, nothing failed.
                    StatusText.Text = $"Saved {Path.GetFileName(result.Path)}, not shared" + (app.Settings.CloudSharingEnabled ? "" : ": cloud sharing is off");
                    SaveButton.IsEnabled = SaveShareButton.IsEnabled = true;
                    await RestoreEditorAsync(replaced);
                    return;
                }
                if (!ok && app.LastShareOutcome != ShareOutcome.FileShareOpened)
                {
                    // Not uploaded, but the trim itself is saved: stay open with the reason instead of flashing "Upload failed" and
                    // closing on a reason only the footer knows. Over Copium's limit the selection can be tightened and saved
                    // again; any other failure is retried from the clip list (saving here again would only trim another copy).
                    if (app.ShareRefusal is string refusal)
                    {
                        string why = refusal.Length > 1 ? char.ToLowerInvariant(refusal[0]) + refusal[1..] : refusal;
                        string tick = app.Settings.CopiumPlus ? "" : ", or tick 'I have Copium+' if you do";
                        StatusText.Text = $"Saved {Path.GetFileName(result.Path)}, not uploaded: {why} - tighten the selection{tick}";
                        SaveButton.IsEnabled = SaveShareButton.IsEnabled = true;
                    }
                    else StatusText.Text = $"Saved {Path.GetFileName(result.Path)}, not uploaded: {app.ShareError ?? "upload failed"} - share it from the clip list to try again";
                    await RestoreEditorAsync(replaced);
                    return;
                }
                // No Copium token: the share window opened with the saved clip, which is the result. Close like a success.
                StatusText.Text = ok ? "Uploaded, link copied" : $"Saved {Path.GetFileName(result.Path)}: drag it from the share window";
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
            await RestoreEditorAsync(replaced);
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// After a save that keeps the window open. When the original was replaced, the editor starts over on the new file
    /// (whole clip, fresh rows, Replace unticked); otherwise the old selection would be cut out of the already-cut file and
    /// the volumes applied a second time. When it was not, only the preview released for the replace attempt comes back.
    /// </summary>
    private async Task RestoreEditorAsync(bool replaced)
    {
        if (_closed) return;
        if (replaced)
        {
            ReplaceCheck.IsChecked = false;
            await OpenAsync(keepStatus: true);
        }
        else if (_playerReleased) await ReopenPreviewAsync();
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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
