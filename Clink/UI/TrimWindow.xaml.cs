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
using Clink.Services.Localization;

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
        // The texts with key names in them (the {l:T} extension takes no arguments), and the toggle's glyph + word.
        PlayButton.ToolTip = L.T("Play / pause ({keys})", ("keys", L.Ltr("Space")));
        SetInButton.ToolTip = L.T("Set the start to the playhead ({keys})", ("keys", L.Ltr("I")));
        SetOutButton.ToolTip = L.T("Set the end to the playhead ({keys})", ("keys", L.Ltr("O")));
        TrimToggle.Content = "✂ " + L.T("Trim");
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
        // With a Copium token as well, the button is split (see UpdateShareButtons); a token added in Settings meanwhile is picked up on Activated.
        void ShowShareButton() => UpdateShareButtons();
        ShowShareButton();
        Activated += (_, _) => UpdateShareButtons();
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
        StatusText.Text = L.T("Opening preview…");
        try
        {
            return await Task.Run(() => new ClipPlayer(path, PreviewMaxWidth));
        }
        catch (Exception ex)
        {
            Log.Warn("Trim preview failed: " + ex.Message);
            StatusText.Text = L.T("Preview unavailable: {error}", ("error", UserText.Of(ex)));
            TracksHint.Text = L.T("No preview; the trim still works.");
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
            TracksPanel.Children.Add(new TextBlock { Text = L.T("This clip has no audio."), Style = (Style)FindResource("MutedText") });
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
            string name = names != null && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"Audio {i + 1}";   // the name in the file (kept by the export; also what the log says)
            string label = TrackLabel(fileRoles[i], names != null && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : L.T("Audio {n}", ("n", i + 1)));   // what the row shows: the file keeps the name "Mix"
            bool feeds = MixPlan.FeedsMix(_roles, i);
            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var include = new CheckBox
            {
                IsChecked = true, Content = label, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = feeds ? L.T("Ticked: {name} is also saved as a track of its own, at the volume on the right. Unticked: no track of its own, but its volume still shapes All audio", ("name", label))
                    : L.T("Ticked: saved in the trimmed clip at the volume on the right. Unticked: left out of the file"),
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
                Content = L.T("Listen"), GroupName = "TrimMonitor", Style = (Style)FindResource("MonitorRadio"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                ToolTip = _roles[i] == TrackRole.Mix && remix
                    ? L.T("Play All audio in the preview, with the Game, Mic and Voice volumes below applied. Players play one track at a time, usually this one")
                    : L.T("Play this track in the preview. Players play one track at a time (usually the first), so the preview does too"),
            };
            listen.Checked += (_, _) => { _monitor = index; SyncPlayerMix(); };
            Grid.SetColumn(listen, 1);
            grid.Children.Add(listen);

            var volume = new Slider
            {
                Minimum = 0, Maximum = 200, Value = 100, Width = 170, SmallChange = 5, LargeChange = 25,
                IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0),
                ToolTip = feeds ? L.T("{name}'s volume, in its own track and inside All audio (right-click resets to 100%)", ("name", label))
                    : _roles[i] == TrackRole.Mix && remix ? L.T("All audio's volume (right-click resets to 100%)")
                    : L.T("Volume in the saved clip (right-click resets to 100%)"),
            };
            var percent = new TextBlock { Text = PercentText(100), FontFamily = new FontFamily("Consolas"), Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            volume.ValueChanged += (_, e) =>
            {
                percent.Text = PercentText(e.NewValue);
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
        string names = JoinNames(sources, or: false);
        bool one = sources.Length == 1;   // the number of sources decides the verb's form (a branch on the number, not a plural category)
        TracksHint.Text = _trimMode
            ? sources.Length > 0 ? L.T(one ? "Ticked tracks are saved at their volume · Listen picks what you hear · {names} also shapes All audio"
                                           : "Ticked tracks are saved at their volume · Listen picks what you hear · {names} also shape All audio", ("names", names))
                                 : L.T("Ticked tracks are saved, each at its volume · Listen picks the one you hear (players play one track, usually the first)")
            : sources.Length > 0 ? L.T(one ? "Listen picks what you hear · the {names} slider changes All audio, live"
                                           : "Listen picks what you hear · the {names} sliders change All audio, live", ("names", names))
                                 : L.T("Listen picks the track you hear (players play one track, usually the first) · the sliders set its volume");
        TracksHint.ToolTip = TracksHint.Text;
    }

    /// <summary>"Game", "Game and Mic", "Game, Mic and Voice" ("or" instead of "and" when <paramref name="or"/>), each name in the language of the UI.</summary>
    private static string JoinNames(IReadOnlyList<TrackRole> roles, bool or)
    {
        if (roles.Count < 2) return string.Join("", roles.Select(RoleText));
        string head = string.Join(L.T("list-separator|, "), roles.Take(roles.Count - 1).Select(RoleText));
        return L.T(or ? "list-or|{head} or {last}" : "list-and|{head} and {last}", ("head", head), ("last", RoleText(roles[^1])));
    }

    /// <summary>The name of one of Clink's own audio sources (the file keeps the English names; the role is what tells them).</summary>
    private static string RoleText(TrackRole role) => role switch
    {
        TrackRole.Game => L.T("track|Game"),
        TrackRole.Mic => L.T("track|Mic"),
        TrackRole.Voice => L.T("track|Voice"),
        TrackRole.Desktop => L.T("track|Desktop"),
        _ => role.ToString(),   // Mix and Other never reach a list
    };

    /// <summary>The sentence for a trim that was refused because the sources inside All audio cannot be changed (the same words as the mix note in the editor).</summary>
    internal static Note AlignmentRefusalNote(TrimAlignmentRefusal kind) => kind == TrimAlignmentRefusal.Untrusted
        ? Note.Of("This Windows reads clips differently: sources cannot be changed inside All audio")
        : Note.Of("Recorded or trimmed by an older Clink: sources cannot be changed inside All audio");

    /// <summary>The label of a track row: "All audio (mix)" for the Mix, the source's name for Clink's own sources, else the name the file has.</summary>
    private static string TrackLabel(TrackRole fileRole, string fileName) => fileRole switch
    {
        TrackRole.Mix => L.T("All audio (mix)"),
        TrackRole.Game or TrackRole.Mic or TrackRole.Voice or TrackRole.Desktop => RoleText(fileRole),
        _ => fileName,
    };

    /// <summary>
    /// The status after an upload that was refused: "Saved X (trim).mp4, not uploaded: over Copium's free limit (200 MB / 10 min) - tighten the
    /// selection, or tick 'I have Copium+' if you do". By <paramref name="kind"/>: over the account's limits is a whole sentence of its own
    /// per plan (the limits are put in it, so no refusal text is cut or re-cased); a smaller copy that could not be made puts the refusal
    /// (<paramref name="refusal"/>, a whole sentence with its own full stop) in a sentence-level slot as it is; a refusal that is only a
    /// phrase (<see cref="ShareRefusalKind.Phrase"/>, a game is in front) ends the status with no advice after it.
    /// </summary>
    internal static string NotUploadedText(string fileName, ShareRefusalKind kind, string refusal, bool copiumPlus)
    {
        string saved = L.Ltr(fileName);
        if (kind == ShareRefusalKind.OverLimit)
        {
            var (maxBytes, maxSeconds, _) = Capabilities.CopiumLimits(copiumPlus);
            return copiumPlus
                ? L.T("Saved {name}, not uploaded: over Copium Plus's limit ({size} / {minutes} min) - tighten the selection", ("name", saved), ("size", L.Bytes(maxBytes)), ("minutes", maxSeconds / 60))
                : L.T("Saved {name}, not uploaded: over Copium's free limit ({size} / {minutes} min) - tighten the selection, or tick '{label}' if you do", ("name", saved), ("size", L.Bytes(maxBytes)), ("minutes", maxSeconds / 60), ("label", L.T("I have Copium+")));
        }
        if (kind == ShareRefusalKind.Phrase)
            return L.T("Saved {name}, not uploaded. {reason}", ("name", saved), ("reason", refusal));
        return copiumPlus
            ? L.T("Saved {name}, not uploaded. {reason} Tighten the selection to share it.", ("name", saved), ("reason", refusal))
            : L.T("Saved {name}, not uploaded. {reason} Tighten the selection, or tick '{label}' if you do.", ("name", saved), ("reason", refusal), ("label", L.T("I have Copium+")));
    }

    /// <summary>A volume as a percentage ("100%"), rounded away from zero like the "0" format did.</summary>
    internal static string PercentText(double value) => L.T("unit|{n}%", ("n", value.ToString("0", L.FormatCulture)));

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
            bool few = _unlined.Length == 1;   // the number decides the wording (a branch, not a plural category)
            string note = L.T(_untrusted ? "This Windows reads clips differently: sources cannot be changed inside All audio"
                                         : "Recorded or trimmed by an older Clink: sources cannot be changed inside All audio");
            older = L.T(few ? "{note}. The {names} volume only changes its own track." : "{note}. The {names} volumes only change their own tracks.",
                ("note", note), ("names", JoinNames(_unlined, or: false)));
        }
        string? baked = null;
        if (missing.Length > 0)
        {
            var present = new[] { TrackRole.Game, TrackRole.Mic, TrackRole.Voice }.Where(r => !missing.Contains(r) && !_unlined.Contains(r)).ToArray();
            bool many = missing.Length > 1;
            baked = L.T(many ? "{missing} are only inside All audio (not saved as tracks of their own), so their volumes cannot be changed. Turning {present} down takes it out of the baked mix again, which can leave a faint trace of it."
                             : "{missing} is only inside All audio (not saved as a track of its own), so its volume cannot be changed. Turning {present} down takes it out of the baked mix again, which can leave a faint trace of it.",
                ("missing", JoinNames(missing, or: false)), ("present", JoinNames(present, or: true)));
        }
        if (older == null && baked == null)
        {
            MixNote.Visibility = Visibility.Collapsed;
            return;
        }
        MixNote.Text = older == null ? baked! : baked == null ? older : older + " " + baked;   // (a space between sentences: S6 moves this to L.JoinSentences)
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
        TitleText.Text = trim ? L.T("Trim · {name}", ("name", L.Ltr(name))) : name;
        Title = TitleText.Text;
        Timeline.ShowSelection = trim;
        var editing = trim ? Visibility.Visible : Visibility.Collapsed;
        SetInButton.Visibility = SetOutButton.Visibility = SaveButtons.Visibility = editing;
        OpenExternalButton.Visibility = trim ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = trim
            ? L.T("Start snaps to keyframes · {space} play · {io} set in/out · {arrows} frame step", ("space", L.Ltr("Space")), ("io", L.Ltr("I/O")), ("arrows", L.Ltr("←/→")))
            : L.T("{space} play / pause · {arrows} frame step · {homeend}", ("space", L.Ltr("Space")), ("arrows", L.Ltr("←/→")), ("homeend", L.Ltr("Home / End")));
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
            try { TestGuard.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => StatusText.Text = L.T("Open failed: {error}", ("error", ex.Message))); }
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
        if (player.LastError != null && _playing) { Pause(); StatusText.Text = L.T("Preview stopped: {error}", ("error", player.LastErrorShown ?? player.LastError)); }
        if (player.AudioIssue is { } audioIssue && !_audioWarned && !_busy) { _audioWarned = true; StatusText.Text = L.T("Preview audio: {error}", ("error", ClipPlayer.AudioIssueText(audioIssue))); }
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
        PosText.ToolTip = _trimMode
            ? L.T("Selection {start} → {end} ({length})", ("start", Fmt(Timeline.SelectionStart)), ("end", Fmt(Timeline.SelectionEnd)), ("length", L.T("unit-short|{n}s", ("n", len.ToString("0.0", L.FormatCulture)))))
            : null;
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

    private void Save_Click(object sender, RoutedEventArgs e) => RunLogged(SaveAsync(share: false));
    private void SaveShare_Click(object sender, RoutedEventArgs e) => RunLogged(SaveAsync(share: true));

    /// <summary>For a save nobody awaits: an exception that got past SaveAsync's own handling is logged, never swallowed or left unobserved.</summary>
    private static void RunLogged(Task task) =>
        task.ContinueWith(t => Log.Error("Trim", t.Exception!.GetBaseException()), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    // The trim a share step has not used up: saved, but the share after it was declined, cancelled, refused or failed. A retry
    // shares this clip instead of trimming again, for as long as what would be written is what was written (OutputKey).
    private ClipEntry? _saved;
    private string _savedKey = "";

    /// <summary>Everything that decides what a trim writes: the source, in / out, the Replace box, and each track's tick and volume.</summary>
    private string OutputKey()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(_entry.Path).Append('|').Append(Timeline.SelectionStart.ToString("R", inv)).Append('|').Append(Timeline.SelectionEnd.ToString("R", inv))
          .Append('|').Append(ReplaceCheck.IsChecked == true ? 'R' : '-');
        foreach (var r in _rows) sb.Append('|').Append(r.Included ? '1' : '0').Append(':').Append(r.Volume.Value.ToString("R", inv));
        return sb.ToString();
    }

    private void RememberSaved(ClipEntry clip)
    {
        if (_closed) return;
        _saved = clip;
        _savedKey = OutputKey();
    }

    /// <summary>The remembered trim when nothing that shapes the output has changed since (and its file is still there); else forgets it.</summary>
    private ClipEntry? SavedToReuse()
    {
        if (_saved is { } clip && _savedKey == OutputKey() && File.Exists(clip.Path)) return clip;
        _saved = null;
        return null;
    }

    private static bool HasToken => AppController.Instance?.HasCopiumToken == true;
    private static bool CloudOn => AppController.Instance?.Settings.CloudSharingEnabled != false;

    /// <summary>"Save &amp; share": the plain button, the split one (a token and cloud sharing on), or neither (cloud sharing off).</summary>
    internal void UpdateShareButtons()
    {
        bool split = ShareChoices.TrimSplit(HasToken, CloudOn);
        SaveShareButton.Visibility = CloudOn && !split ? Visibility.Visible : Visibility.Collapsed;
        SaveShareSplit.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Save, trim and share buttons on or off together: both halves of the split button as well as the plain one.</summary>
    private void SetSaveEnabled(bool enabled) =>
        SaveButton.IsEnabled = SaveShareButton.IsEnabled = SaveShareMain.IsEnabled = SaveShareArrow.IsEnabled = enabled;

    /// <summary>The "▾" half: the menu with the other options.</summary>
    private void SaveShareArrow_Click(object sender, RoutedEventArgs e) => OpenShareMenu();

    /// <summary>Alt+Down / Shift+F10 on the "Save &amp; share" half open the menu too.</summary>
    private void SaveShareMain_KeyDown(object sender, KeyEventArgs e)
    {
        if (SaveShareSplit.Visibility != Visibility.Visible || !ShareMenu.IsOpenKey(e)) return;
        e.Handled = true;
        OpenShareMenu();
    }

    private void OpenShareMenu()
    {
        if (_busy || ShareChoices.ForTrim(HasToken, CloudOn) is not { } items) return;
        // PlacementTarget is the arrow, which is in this window: CaptureExclusion finds the window through it (a capture-excluded
        // trim window keeps its menu out of capture too); the menu drops from the left edge of the whole split button
        ShareMenu.Open(SaveShareArrow, items, choice => RunLogged(SaveAsync(share: true, choice)), below: SaveShareMain);
    }

    /// <summary>
    /// "Share file…" and "Save a copy…" on the saved trim. True when it went through (the window then closes like after an upload);
    /// false leaves the window open with the reason in the status line: the trim itself is saved either way.
    /// </summary>
    private async Task<bool> RunFileChoiceAsync(AppController app, ClipEntry saved, ShareChoice choice)
    {
        string name = L.Ltr(Path.GetFileName(saved.Path));
        if (choice == ShareChoice.ShareFile)
        {
            if (app.ShareFile(saved)) { StatusText.Text = L.T("Saved {name}: drag it from the share window", ("name", name)); return true; }
            StatusText.Text = L.T("Saved {name}, not shared: could not open the share window. Choose again to share it.", ("name", name));
            return false;
        }
        StatusText.Text = L.T("Saving a copy…");
        var progress = new Progress<double>(p => StatusText.Text = L.T("Saving a copy… {percent}", ("percent", p.ToString("P0", L.FormatCulture))));
        var result = await app.SaveCopyAsync(saved, this, progress);
        switch (result.Outcome)
        {
            case SaveOutcome.Saved:
                string dir = Path.GetDirectoryName(result.SavedTo) ?? "";
                StatusText.Text = L.T("Saved {name}; copy saved to {folder}", ("name", name), ("folder", L.Ltr(Path.GetFileName(dir) is { Length: > 0 } leaf ? leaf : dir)));
                return true;
            case SaveOutcome.Cancelled:
                StatusText.Text = L.T("Saved {name}, no copy saved. Choose again to share it.", ("name", name));
                return false;
            default:
                StatusText.Text = L.T("Saved {name}, copy failed: {error}. Choose again to share it.", ("name", name), ("error", result.Error));
                return false;
        }
    }

    /// <summary>
    /// Trims and saves. <paramref name="share"/> then shares the saved clip: with <paramref name="choice"/> null, what "Save &amp; share"
    /// always did (upload, or the share window without a token); with a choice from the ▾ menu, that choice. Nothing is shared when
    /// the save did not go through.
    /// </summary>
    internal async Task SaveAsync(bool share, ShareChoice? choice = null)
    {
        if (_busy) return;
        _busy = true;
        SetSaveEnabled(false);
        string src = _entry.Path;
        string dst = src; // set once the output's name is chosen; the catch only deletes a file that is not the source
        bool trimmed = false; // once true the output is complete and is never deleted, whatever happens after
        bool replaced = false; // the original is now the trim: what the editor shows describes a file that is gone
        ClipEntry? result = null; // the saved clip, once there is one (a new trim, or the remembered one on a retry)
        // The share step did not complete: stay open, buttons back on, and remember the saved clip so choosing again shares it.
        async Task KeepOpenAsync(bool enableButtons = true)
        {
            if (enableButtons) SetSaveEnabled(true);
            await RestoreEditorAsync(replaced);
            if (share && result != null) RememberSaved(result);   // after the editor reopened on a replaced file: the key is that state's
        }
        try
        {
            Pause();
            var app = AppController.Instance ?? throw new ShownException(Note.Of("Clink is not running"));
            // A retry after a share step that did not finish shares the clip that step saved: nothing that shapes the output changed.
            // "Save trimmed" is an explicit save: always a new trim, and the memory goes.
            ClipEntry? reuse = share ? SavedToReuse() : null;
            if (reuse == null) _saved = null;
            string? replaceProblem = null;
            if (reuse != null)
            {
                result = reuse;
                trimmed = true;
                dst = reuse.Path;
                StatusText.Text = L.T("Sharing {name}", ("name", L.Ltr(Path.GetFileName(reuse.Path))));
            }
            else
            {
                StatusText.Text = L.T("Trimming…");
                double start = Timeline.SnapToKeyframe(Timeline.SelectionStart), end = Timeline.SelectionEnd;
                bool replace = ReplaceCheck.IsChecked == true;
                // Trims are written to the local clips folder (docs/SPEC-v2.md §9: local first, the archive service moves them later
                // under the normal rules). A source that was archived, or that lives elsewhere, is not a reason to write next to it:
                // a trim written onto the share would be a plain local entry with a UNC path, checked with File.Exists on the UI thread.
                string srcDir = Path.GetDirectoryName(src) ?? app.Settings.ClipsFolder;
                string dir = _entry.IsArchived || !ArchiveService.IsSameFolder(srcDir, app.Settings.ClipsFolder) ? app.Settings.ClipsFolder : srcDir;
                string stem = Path.GetFileNameWithoutExtension(src);
                dst = Path.Combine(dir, stem + " (trim).mp4");
                int n = 2;
                while (File.Exists(dst)) dst = Path.Combine(dir, stem + $" (trim {n++}).mp4");
                if (end - start < 0.1) throw new ShownException(Note.Of("Selection is too short"));
                TestGuard.Check(dst);
                Directory.CreateDirectory(dir);
                // The rows' names (file order, as the mix); without rows the trimmer reads them from the file itself.
                string[]? names = _rows.Count > 0 ? _rows.Select(r => r.Name).ToArray() : null;
                // What the sliders did to All audio is rendered into it here (MixPlan), as the preview plays it.
                TrackMix[]? mix = _rows.Count > 0 ? MixPlan.Export(_roles, _rows.Select(r => r.Included).ToArray(), _rows.Select(r => r.Gain).ToArray()) : null;
                if (mix != null && !mix.Any(m => m.Include)) StatusText.Text = L.T("Trimming (no audio)…");
                string trimDst = dst;
                string[] keptNames;
                try { keptNames = await Task.Run(() => Trimmer.Trim(src, trimDst, start, end, names, mix)); }
                catch (TrimAlignmentException refused) { throw new ShownException(AlignmentRefusalNote(refused.Kind)); }   // the kind picks the sentence, not the words of the message
                trimmed = true;
                replaceProblem = replace ? await ReplaceOriginalAsync(src, dst) : null;
                if (replace && replaceProblem == null)
                {
                    replaced = true;
                    _entry.Duration = end - start;
                    _entry.Size = new FileInfo(src).Length;
                    _entry.AudioTracks = keptNames;
                    _entry.CopiumLink = null; _entry.CopiumPageUrl = null; _entry.CopiumId = null;
                    app.Library.Update(_entry);
                    result = _entry;
                    StatusText.Text = L.T("Replaced {name}", ("name", L.Ltr(Path.GetFileName(src))));
                }
                else
                {
                    result = app.Library.Add(dst, end - start, new FileInfo(dst).Length, _entry.Game, keptNames, isTrim: true, sourcePath: src);
                    StatusText.Text = replaceProblem != null
                        ? L.T("Could not replace the original ({reason}); saved as {name} instead", ("reason", replaceProblem), ("name", L.Ltr(Path.GetFileName(dst))))
                        : L.T("Saved {name}", ("name", L.Ltr(Path.GetFileName(dst))));
                }
            }
            if (share && choice is ShareChoice.ShareFile or ShareChoice.SaveCopy)
            {
                // from the ▾ menu: the trim is saved (any failure above skipped this); now the chosen option on the new clip
                if (!await RunFileChoiceAsync(app, result, choice!.Value))
                {
                    await KeepOpenAsync();
                    return;
                }
            }
            else if (share)
            {
                StatusText.Text = L.T("Uploading…");
                var progress = new Progress<double>(p => StatusText.Text = app.ShareProgressText(result, p));
                bool ok = await app.ShareAsync(result, progress, context: ConsentContext.TrimShare);
                if (!ok && app.LastShareOutcome == ShareOutcome.Declined)
                {
                    // "Not now" in the Share with Copium? pop-up, or cloud sharing off: the trim is saved, nothing failed.
                    bool cloudOff = !app.Settings.CloudSharingEnabled;
                    StatusText.Text = L.T(cloudOff ? "Saved {name}, not shared: cloud sharing is off" : "Saved {name}, not shared. Choose again to share it.", ("name", L.Ltr(Path.GetFileName(result.Path))));
                    await KeepOpenAsync();
                    return;
                }
                if (!ok && app.LastShareOutcome != ShareOutcome.FileShareOpened)
                {
                    // Not uploaded, but the trim itself is saved: stay open with the reason instead of flashing "Upload failed" and
                    // closing on a reason only the footer knows. Over Copium's limit the selection can be tightened and saved
                    // again; for any other failure choosing again retries the upload on the saved trim (it is remembered, so nothing
                    // is trimmed twice), which is why the buttons come back on here too.
                    if (app.ShareRefusal is string refusal)
                    {
                        StatusText.Text = NotUploadedText(Path.GetFileName(result.Path), app.RefusalKind, refusal, app.Settings.CopiumPlus);
                    }
                    else StatusText.Text = L.T("Saved {name}, not uploaded: {error}. Choose again to retry.", ("name", L.Ltr(Path.GetFileName(result.Path))), ("error", app.ShareError ?? L.T("upload failed")));
                    await KeepOpenAsync();
                    return;
                }
                // No Copium token: the share window opened with the saved clip, which is the result. Close like a success.
                StatusText.Text = ok ? L.T("Uploaded, link copied") : L.T("Saved {name}: drag it from the share window", ("name", L.Ltr(Path.GetFileName(result.Path))));
            }
            await Task.Delay(replaceProblem != null ? 3000 : 1200);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Trim", ex);
            if (!trimmed)
            {
                StatusText.Text = L.T("Trim failed: {error}", ("error", UserText.Of(ex)));
                try { if (File.Exists(dst) && !string.Equals(dst, src, StringComparison.OrdinalIgnoreCase)) File.Delete(dst); } catch { }
            }
            else
            {
                // The trim itself finished: keep the file and say where it is.
                string kept = File.Exists(dst) ? dst : src;
                StatusText.Text = L.T(share && result != null ? "Saved {name}, but: {error}. Choose again to share it." : "Saved {name}, but: {error}",
                    ("name", L.Ltr(Path.GetFileName(kept))), ("error", UserText.Of(ex)));
            }
            try { await KeepOpenAsync(); }
            catch (Exception ex2) { Log.Error("Trim", ex2); }   // the window must not be left disabled by a failure in the recovery itself
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
        if (!released) return L.T("the preview is still reading it");
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
            return ex.HResult is SharingViolation or LockViolation ? L.T("file in use") : ex.Message;
        }
    }

    /// <summary>A failure whose text the user sees: <see cref="Exception.Message"/> is the note's English (the log), <see cref="UserText.Of"/> the note in the UI language.</summary>
    private sealed class ShownException : InvalidOperationException
    {
        public ShownException(Note note) : base(note.English) { UserText.Attach(this, note); }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
