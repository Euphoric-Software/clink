using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clink.Audio;
using Clink.Native;
using Clink.Services;
using Clink.UI;

namespace Clink;

/// <summary>A combo entry: label + value; <see cref="Header"/> rows are section titles, <see cref="Disabled"/> rows are greyed (a codec this GPU lacks).</summary>
internal sealed record Choice(string Label, object Value, bool Header = false, bool Disabled = false) { public override string ToString() => Label; }

/// <summary>
/// The main window (docs/SPEC-v2.md §1, §10): resizable, settings tabs and the clips pane, which sits in a right column when the
/// window is ≥ 900 px wide and becomes a fourth tab when it is narrower. Capture holds, top to bottom, Capturing (monitor,
/// quality, size target, cursor) · Audio tracks (under a "set up your audio" banner that goes away for good once the group is
/// hovered over or changed) · Recording · Replay length (the slider with the one size/RAM readout under it) · Hotkeys; Clip holds
/// Copium and Storage; Alerts holds Toasts, Status dot and Updates (with "run at startup"). Every control writes its setting on
/// change and, where the engine is affected, restarts it after a short debounce (<see cref="ScheduleRestart"/>).
/// </summary>
public partial class MainWindow : Window
{
    private const double WideWidth = 900;

    private readonly AppController _app = AppController.Instance;
    private readonly ObservableCollection<string> _customApps = new();
    private readonly DispatcherTimer _restartTimer, _saveTimer;
    private readonly Choice[] _bitratePresets = new[] { 4, 6, 8, 12, 16, 20, 25, 30, 40, 50, 60, 80, 100 }.Select(b => new Choice($"{b} Mbps", b)).ToArray();
    private bool _loading = true;
    private bool _filling;                              // a combo is being (re)populated: its SelectionChanged is not a user choice
    private bool _hintShown;
    private bool _checkingUpdate;
    private bool _wide, _layoutApplied;
    private DateTime _noticeUntil = DateTime.MinValue;   // keep a Say() notice visible this long before the stats line takes the footer back

    public MainWindow()
    {
        InitializeComponent();
        _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _restartTimer.Tick += (_, _) => { _restartTimer.Stop(); _app.StartEngine(); };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); if (!_loading) _app.Settings.Save(); };
        CustomAppsList.ItemsSource = _customApps;

        var s = _app.Settings;
        RestoreWindowBounds(s);
        Loaded += (_, _) => { ApplyLayout(); Populate(); };
        Closing += (_, e) => { e.Cancel = true; HideToTray(); };
        LocationChanged += (_, _) => { if (!_loading && IsVisible && WindowState == WindowState.Normal) { s.WindowX = Left; s.WindowY = Top; ScheduleSave(); } };
        SizeChanged += (_, _) =>
        {
            ApplyLayout();
            if (!_loading && IsVisible && WindowState == WindowState.Normal) { s.WindowWidth = ActualWidth; s.WindowHeight = ActualHeight; ScheduleSave(); }
        };

        _app.StateChanged += RefreshStatus;
        _app.Notice += msg => Dispatcher.BeginInvoke(() => { FooterText.Text = msg; _noticeUntil = DateTime.UtcNow.AddSeconds(4); });
        _app.ClipSaved += _ => Dispatcher.BeginInvoke(() => Clips.Refresh());
        _app.ShowRequested += () => Dispatcher.BeginInvoke(ShowFromTray);
        _app.TrimLastRequested += () => Dispatcher.BeginInvoke(async () =>
        {
            // File.Exists on an archived entry is a share round trip that can stall for the SMB timeout: look on a worker.
            var entries = _app.Library.Entries;
            ClipEntry? newest = null;
            try { newest = await Task.Run(() => entries.FirstOrDefault(e => e.Exists)); }
            catch (Exception ex) { Log.Warn("trim last: " + ex.Message); }
            if (newest != null) OpenTrim(newest); else _app.Say("No clips to trim yet");
        });
        _app.TrimRequested += entry => Dispatcher.BeginInvoke(() => OpenTrim(entry));
        _app.Library.Changed += () => Dispatcher.BeginInvoke(() => Clips.Refresh());
        _app.UpdateStateChanged += () => Dispatcher.BeginInvoke(RefreshUpdateState);
        _app.Archive.Changed += () => Dispatcher.BeginInvoke(RefreshArchiveStatus);
        Clips.TrimRequested += OpenTrim;
        HotkeyEditor.Changed += HotkeysChanged;
        ReplaySlider.ValueChanged += v => { if (!_loading) UpdateEstimates(v); };
        ReplaySlider.ValueCommitted += ReplayLengthCommitted;
    }

    // ------------------------------------------------------------------ window bounds / layout

    private void RestoreWindowBounds(Settings s)
    {
        if (s.WindowWidth is double ww && ww >= MinWidth) Width = ww;
        if (s.WindowHeight is double wh && wh >= MinHeight) Height = wh;
        if (s.WindowX is double wx && s.WindowY is double wy && !double.IsNaN(wx) && !double.IsNaN(wy))
        {
            // Only when some of the window would still be on a screen (a monitor may have gone away since).
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
            if (wx + 100 < vr && wx + Width > vl + 100 && wy + 40 < vb && wy >= vt - 10)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = wx; Top = wy;
            }
        }
    }

    /// <summary>Wide: settings tabs left, the clips pane in a right column. Narrow: the clips pane is the fourth tab.</summary>
    private void ApplyLayout()
    {
        bool wide = ActualWidth >= WideWidth;
        if (_layoutApplied && wide == _wide) return;
        _wide = wide; _layoutApplied = true;
        if (wide)
        {
            ClipsTab.Content = null;
            if (ReferenceEquals(Tabs.SelectedItem, ClipsTab)) Tabs.SelectedIndex = 0;
            ClipsTab.Visibility = Visibility.Collapsed;
            RightHost.Child = Clips;
            Clips.Margin = new Thickness(0);
            RightHost.Visibility = Visibility.Visible;
            SplitCol.Width = new GridLength(16);
            RightCol.Width = new GridLength(1.15, GridUnitType.Star);
        }
        else
        {
            RightHost.Child = null;
            RightHost.Visibility = Visibility.Collapsed;
            SplitCol.Width = new GridLength(0);
            RightCol.Width = new GridLength(0);
            Clips.Margin = new Thickness(0, 8, 0, 0);
            ClipsTab.Content = Clips;
            ClipsTab.Visibility = Visibility.Visible;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ------------------------------------------------------------------ populate

    private void Populate()
    {
        var s = _app.Settings;
        MonitorMap.SetMonitors(_app.Monitors, _app.SelectedMonitor?.DeviceName);
        MonitorMap.Selected += m => { s.MonitorDevice = m.DeviceName; s.Save(); UpdateMonitorLabel(); FillCodecs(); UpdateEstimates(); _app.ApplyStatusDot(); ScheduleRestart(); };
        UpdateMonitorLabel();

        Fill(ResolutionCombo, new[] { new Choice("Native", "native"), new Choice("1440p", "1440p"), new Choice("1080p", "1080p"), new Choice("720p", "720p") }, s.Resolution);
        Fill(FpsCombo, new[] { new Choice("30", 30), new Choice("60", 60), new Choice("120", 120) }, s.Fps);
        Fill(BitrateCombo, _bitratePresets, s.BitrateMbps);
        FillCodecs();
        Fill(SizeTargetCombo, new[] { "none", "discord10", "discord50", "copium200", "discord500" }.Select(t => new Choice(Capabilities.SizeTargetName(t), t)).ToArray(), s.SizeTarget);
        Fill(ModeCombo, new[] { new Choice("Only while a game or custom app is running", "game"), new Choice("Always (whenever Clink runs)", "always") }, s.RecordingMode);
        _customApps.Clear();
        foreach (var a in (s.CustomApps ?? new()).Select(GameProcessTracker.NormalizeName).Where(a => a.Length > 0 && !IsAuto(a)).Distinct(StringComparer.OrdinalIgnoreCase)) _customApps.Add(a);
        FillRunningApps();

        HotkeyEditor.Load(s.Hotkeys, s.BufferSeconds);
        ReplaySlider.Value = Math.Clamp(s.BufferSeconds, ReplayLengthSlider.Minimum, ReplayLengthSlider.Maximum);
        ClickSoundCheck.IsChecked = s.ClickSound;
        CursorCheck.IsChecked = s.CaptureCursor;
        StartupCheck.IsChecked = s.RunAtStartup;
        TrackDesktopCheck.IsChecked = s.TrackDesktop;
        TrackGameCheck.IsChecked = s.TrackGame;
        TrackMicCheck.IsChecked = s.TrackMic;
        TrackVoiceCheck.IsChecked = s.TrackVoice;

        FillDesktopCombo(s.AllDeviceId);
        FillGameCombo(s.GameProcess);
        FillDeviceCombo(MicCombo, true, s.MicDeviceId);
        FillVoiceCombo(s.VoiceApp);
        UpdateAudioEnabled();
        AudioSetupBanner.Visibility = s.AudioSetupSeen ? Visibility.Collapsed : Visibility.Visible;

        if (!string.IsNullOrEmpty(s.CopiumToken)) { TokenBox.Password = s.CopiumToken; CopiumStatus.Text = "Token saved"; CopiumStatus.Foreground = (Brush)FindResource("Good"); }
        TokenSaveButton.IsEnabled = false;
        CopiumPlusCheck.IsChecked = s.CopiumPlus;

        ClipsFolderBox.Text = s.ClipsFolder;
        ArchiveCheck.IsChecked = s.ArchiveEnabled;
        ArchivePathBox.Text = s.ArchivePath ?? "";
        ArchiveDaysBox.Text = s.ArchiveAfterDays.ToString(CultureInfo.InvariantCulture);
        ArchiveGbBox.Text = s.ArchiveWhenLocalExceedsGB.ToString("0.##", CultureInfo.InvariantCulture);
        UpdateArchiveEnabled();
        RefreshArchiveStatus();

        Fill(ToastPositionCombo, new[]
        {
            new Choice("Top left", "top-left"), new Choice("Top", "top"), new Choice("Top right", "top-right"),
            new Choice("Left", "left"), new Choice("Right", "right"),
            new Choice("Bottom left", "bottom-left"), new Choice("Bottom", "bottom"), new Choice("Bottom right", "bottom-right"),
        }, s.ToastPosition);
        ToastClipCheck.IsChecked = s.ToastClip.Enabled; ToastClipSlider.Value = Math.Clamp(s.ToastClip.Seconds, 1, 30);
        ToastSharedCheck.IsChecked = s.ToastShared.Enabled; ToastSharedSlider.Value = Math.Clamp(s.ToastShared.Seconds, 1, 30);
        ToastRecordingCheck.IsChecked = s.ToastRecording.Enabled; ToastRecordingSlider.Value = Math.Clamp(s.ToastRecording.Seconds, 1, 30);
        UpdateToastLabels();
        DotCheck.IsChecked = s.StatusDot;
        Fill(DotPositionCombo, new[] { new Choice("Top left", "top-left"), new Choice("Top right", "top-right"), new Choice("Bottom left", "bottom-left"), new Choice("Bottom right", "bottom-right") }, s.StatusDotPosition);
        DotPositionCombo.IsEnabled = s.StatusDot;

        AutoUpdateCheck.IsChecked = s.AutoUpdate;
        VersionText.Text = "v" + _app.Updater.VersionDisplay;
        UpdateVersionText.Text = "Clink v" + _app.Updater.VersionDisplay;
        VersionText.ToolTip = _app.Updater.Repo is string repo
            ? $"Updates come from github.com/{repo}{(_app.Updater.RepoOverridden ? " (UpdateRepo in settings.json)" : "")} · click to check now"
            : "Updates are not configured in this build";
        RefreshUpdateState();

        UpdateEstimates();
        Clips.Refresh();
        RefreshStatus();
        _loading = false;
    }

    private static void Fill(ComboBox combo, Choice[] choices, object current)
    {
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => !c.Header && Same(c.Value, current)) ?? choices.FirstOrDefault(c => !c.Header && !c.Disabled) ?? choices[0];
    }

    // string values (device ids, process names) compare case-insensitively so a hand-edited settings.json still matches
    private static bool Same(object a, object b) => a is string sa && b is string sb ? string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase) : Equals(a, b);

    private static T? Selected<T>(ComboBox combo) => combo.SelectedItem is Choice c && c.Value is T v ? v : default;

    private void Refill(ComboBox combo, List<Choice> items, object current)
    {
        bool was = _filling;
        _filling = true;
        try { Fill(combo, items.ToArray(), current); }
        finally { _filling = was; }
    }

    // ------------------------------------------------------------------ capture: codecs, size estimates & targets (§3, §4)

    private void FillCodecs()
    {
        var items = Capabilities.Codecs
            .Select(c => new Choice(c.Supported ? c.Name + (c.Experimental ? " (experimental)" : "") : $"{c.Name} — {c.Reason}", c.Id, Disabled: !c.Supported))
            .ToList();
        Refill(CodecCombo, items, _app.Settings.Codec);
        UpdateCodecDescription();
    }

    private void UpdateCodecDescription()
    {
        string id = Selected<string>(CodecCombo) ?? _app.Settings.Codec;
        var info = Capabilities.Codec(id);
        if (info == null) { CodecDescription.Text = ""; return; }
        if (!Capabilities.Codecs.Any(c => c.Supported))
        {
            CodecDescription.Text = "No hardware video encoder found on this GPU – update the GPU driver; recording is not possible.";
            return;
        }
        var mon = _app.SelectedMonitor;
        var (w, h) = mon != null ? AppController.OutputSize(mon, _app.Settings.Resolution) : (0u, 0u);
        var fallback = Capabilities.Codec(Capabilities.EffectiveCodec(id, w, h));
        if (info.Supported && fallback != null && fallback.Id != info.Id)
            CodecDescription.Text = $"{info.Name} cannot encode {w}×{h}; recording uses {fallback.Name} instead. {info.Name} – {info.Description}";
        else if (info.Supported)
            CodecDescription.Text = $"{info.Name} – {info.Description}{(info.EncoderName != null ? $"  ·  {info.EncoderName}" : "")}";
        else
            CodecDescription.Text = $"{info.Name} is {info.Reason}; recording uses {fallback?.Name ?? "H.264"} instead. {info.Name} – {info.Description}";
    }

    private int AudioTrackCount() => _app.AudioTrackCount;   // the mix is always track 1; the same count the engine's bitrate is resolved with

    /// <summary>
    /// The readout under the replay-length slider (§10, the only place size and RAM are shown): per-minute / per-clip sizes
    /// (<see cref="EstimateText"/>), the buffer's RAM (<see cref="RamText"/>, Danger over half of it) and the size-target note
    /// (<see cref="SizeNote"/>, Warn when no bitrate can make the clip fit); plus the bitrate combo's tooltip and the slider's RAM
    /// limit. Called on every resolution / FPS / bitrate / codec / size-target / audio-track / length change.
    /// </summary>
    private void UpdateEstimates(int? previewSeconds = null)
    {
        var s = _app.Settings;
        int seconds = Math.Clamp(previewSeconds ?? s.BufferSeconds, 1, 3600);
        int tracks = AudioTrackCount();
        var (mbps, note) = Capabilities.ResolveBitrate(s, tracks, seconds);
        bool targeted = Capabilities.SizeTargetBytes(s.SizeTarget) > 0;

        bool was = _filling;
        _filling = true;
        try
        {
            // The chosen bitrate stays editable; a size target only lowers the encode bitrate when a clip would not fit.
            if (!ReferenceEquals(BitrateCombo.ItemsSource, _bitratePresets)) Fill(BitrateCombo, _bitratePresets, s.BitrateMbps);
            BitrateCombo.IsEnabled = true;
            BitrateCombo.ToolTip = targeted && mbps < s.BitrateMbps
                ? $"Encoding at {mbps:0.#} Mbps so a {Capabilities.FormatDuration(Capabilities.FitSeconds(s.SizeTarget, seconds))} clip fits the size target; set the target to None to use the full {s.BitrateMbps} Mbps"
                : null;
        }
        finally { _filling = was; }

        SizeNote.Text = note ?? "";
        SizeNote.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;
        // Warn colour when no bitrate can make the clip fit, or when the Copium target's 10 min is exceeded (no bitrate fixes that either).
        SizeNote.Foreground = (Brush)FindResource(note != null && (note.Contains("does not fit") || note.Contains(Capabilities.CopiumLengthNote)) ? "Warn" : "Muted");
        EstimateText.Text = Capabilities.DescribeEstimate(mbps, tracks, seconds);

        var (ramText, tooMuch) = Capabilities.DescribeRing(mbps, seconds);
        RamText.Text = ramText;
        RamText.Foreground = (Brush)FindResource(tooMuch ? "Danger" : "Muted");
        // Longer than the machine's RAM can hold at all cannot be allocated: the slider refuses it (the engine clamps to half anyway).
        long ram = Capabilities.PhysicalRamBytes();
        ReplaySlider.Limit = ram > 0 ? Math.Max(ReplayLengthSlider.Minimum, Capabilities.RingSecondsFor(mbps, ram)) : ReplayLengthSlider.Maximum;
    }

    private void SizeTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        _app.Settings.SizeTarget = Selected<string>(SizeTargetCombo) ?? "none";
        _app.Settings.Save();
        UpdateEstimates();
        ScheduleRestart();
    }

    // ------------------------------------------------------------------ capture: recording mode & custom apps (§5)

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        _app.Settings.RecordingMode = Selected<string>(ModeCombo) ?? "game";
        _app.Settings.Save();
        RefreshStatus();
        ScheduleRestart();   // StartEngine() stops the engine when game-only and no game runs, starts it otherwise
    }

    private void FillRunningApps()
    {
        var items = new List<Choice> { new("Add running app…", "", true) };
        var apps = RunningApps().Where(c => !_customApps.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        items.AddRange(apps.Select(c => new Choice(AppLabel(c), c.Name)));
        if (apps.Count == 0) items.Add(new Choice("No other windowed apps running", "", true));
        bool was = _filling;
        _filling = true;
        try { RunningAppsCombo.ItemsSource = items; RunningAppsCombo.SelectedIndex = 0; }
        finally { _filling = was; }
    }

    private void RunningAppsCombo_DropDownOpened(object sender, EventArgs e) => FillRunningApps();

    private void RunningAppsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        if (Selected<string>(RunningAppsCombo) is { Length: > 0 } name) AddCustomApp(name);
        Dispatcher.BeginInvoke(FillRunningApps);   // back to the placeholder once the selection event has finished
    }

    private void CustomAppBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddApp_Click(sender, e);
        e.Handled = true;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        string name = CustomAppBox.Text;
        if (string.IsNullOrWhiteSpace(name)) return;
        AddCustomApp(name);
        CustomAppBox.Text = "";
    }

    private void AddCustomApp(string raw)
    {
        string name = GameProcessTracker.NormalizeName(Path.GetFileName(raw.Trim().Trim('"')));
        if (name.Length == 0 || IsAuto(name)) return;
        if (!_customApps.Contains(name, StringComparer.OrdinalIgnoreCase)) _customApps.Add(name);
        SaveCustomApps();
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string name) { _customApps.Remove(name); SaveCustomApps(); }
    }

    private void SaveCustomApps()
    {
        if (_loading) return;
        _app.Settings.CustomApps = _customApps.ToList();
        _app.Settings.Save();   // the 1 s detection poll picks the list up on its next pass
    }

    // ------------------------------------------------------------------ audio sources (unchanged from v1)

    private static bool IsAuto(string? name) => GameProcessTracker.IsAuto(name);

    /// <summary>
    /// All audio / Mic: the default endpoint or any active one. A saved device that is unplugged or disabled right now stays
    /// selected (by name when Windows still knows it) rather than silently showing "Default", so the picker tells the truth
    /// about what the engine is configured with and the id survives until the user picks something else.
    /// </summary>
    private void FillDeviceCombo(ComboBox combo, bool capture, string? current)
    {
        var items = new List<Choice> { new(capture ? "Default microphone" : "Default output", "") };
        List<AudioDeviceInfo> devices;
        try { devices = WasapiSource.ListDevices(capture, includeInactive: true); } catch { devices = new(); }
        items.AddRange(devices.Where(d => d.Problem == null).Select(d => new Choice(d.Name, d.Id)));
        string id = current ?? "";
        if (id.Length > 0 && !items.Any(i => Same(i.Value, id)))
        {
            var known = devices.FirstOrDefault(d => Same(d.Id, id));
            items.Add(new Choice(known != null ? $"{known.Name} ({known.Problem})" : "Saved device (not connected)", id));
        }
        Refill(combo, items, id);
    }

    private static List<GameProcessTracker.Candidate> RunningApps()
    {
        try { return GameProcessTracker.ListCandidates(); } catch { return new(); }
    }

    private static string AppLabel(GameProcessTracker.Candidate c)
    {
        string title = c.Title.Trim();
        if (title.Length > 48) title = title[..47] + "…";
        return title.Length == 0 ? c.Name : $"{c.Name} — {title}";
    }

    /// <summary>Game track: auto-detect or a running app; a configured name that is not running stays selectable so it is not lost.</summary>
    private void FillGameCombo(string current)
    {
        current = GameProcessTracker.NormalizeName(current);   // "auto" in any case, ".exe" dropped: matches the way the engine reads it
        var items = new List<Choice> { new("Auto (fullscreen app on the captured monitor)", "auto") };
        var apps = RunningApps();
        if (apps.Count > 0) items.Add(new Choice("Running apps", "", true));
        items.AddRange(apps.Select(c => new Choice(AppLabel(c), c.Name)));
        AddDeviceChoices(items, current);
        if (!IsAuto(current) && !IsDeviceValue(current) && !items.Any(i => Same(i.Value, current))) items.Add(new Choice($"{current} (not running)", current));
        Refill(GameCombo, items, current);
    }

    /// <summary>Voice track: auto (first known voice app found), one of the known names, or any other running app.</summary>
    private void FillVoiceCombo(string current)
    {
        current = GameProcessTracker.NormalizeName(current);
        // Short enough to fit the closed box at the window's width; the full list is in the combo's tooltip.
        var items = new List<Choice> { new("Auto (Discord, TeamSpeak, …)", "auto") };
        items.AddRange(GameProcessTracker.VoiceAppNames.Select(n => new Choice(n, n)));
        var others = RunningApps().Where(c => !items.Any(i => Same(i.Value, c.Name))).ToList();
        if (others.Count > 0) items.Add(new Choice("Other running apps", "", true));
        items.AddRange(others.Select(c => new Choice(AppLabel(c), c.Name)));
        AddDeviceChoices(items, current);
        if (!IsAuto(current) && !IsDeviceValue(current) && !items.Any(i => Same(i.Value, current))) items.Add(new Choice($"{current} (not running)", current));
        Refill(VoiceCombo, items, current);
    }

    // The device and process lists are refreshed every time a dropdown opens (headsets get plugged in, games get launched while
    // the window sits in the tray); the current selection is always kept (see FillDeviceCombo / FillGameCombo).
    private void AllDeviceCombo_DropDownOpened(object sender, EventArgs e) => FillDesktopCombo(Selected<string>(AllDeviceCombo) ?? _app.Settings.AllDeviceId);

    /// <summary>
    /// Desktop track: the default output's loopback, any output's loopback ("out:&lt;id&gt;") or a capture device ("in:&lt;id&gt;":
    /// a Wave Link mix, a virtual cable), like the Game and Voice pickers. A bare endpoint id from an older settings file is
    /// an output loopback and keeps working; a saved device that is not connected stays selected rather than silently
    /// becoming "Default output".
    /// </summary>
    private void FillDesktopCombo(string? current)
    {
        string id = current ?? "";
        var items = new List<Choice> { new("Default output (what you hear)", "") };
        AddDeviceChoices(items, id);
        if (id.Length > 0 && !items.Any(i => Same(i.Value, id)))
        {
            List<AudioDeviceInfo> outputs;
            try { outputs = WasapiSource.ListDevices(false, includeInactive: true); } catch { outputs = new(); }
            var known = outputs.FirstOrDefault(d => Same(d.Id, id));   // v1 value: a bare render endpoint id
            items.Add(new Choice(known != null ? "Output: " + known.Name + (known.Problem != null ? $" ({known.Problem})" : "") : "Saved device (not connected)", id));
        }
        Refill(AllDeviceCombo, items, id);
    }

    private void GameCombo_DropDownOpened(object sender, EventArgs e) => FillGameCombo(Selected<string>(GameCombo) ?? _app.Settings.GameProcess);

    private void MicCombo_DropDownOpened(object sender, EventArgs e) => FillDeviceCombo(MicCombo, true, Selected<string>(MicCombo) ?? _app.Settings.MicDeviceId);

    private void VoiceCombo_DropDownOpened(object sender, EventArgs e) => FillVoiceCombo(Selected<string>(VoiceCombo) ?? _app.Settings.VoiceApp);

    private void UpdateAudioEnabled()
    {
        // Game, mic and voice are always captured for the mix, so their pickers stay live; only the optional Desktop track's does not.
        if (AllDeviceCombo == null || TrackDesktopCheck == null) return;
        AllDeviceCombo.IsEnabled = TrackDesktopCheck.IsChecked == true;
    }

    private static bool IsDeviceValue(string? v) =>
        v != null && (v.StartsWith("in:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("out:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Audio devices as alternative sources for the game / voice tracks: capture endpoints (virtual inputs such as Wave Link mixes) and output loopbacks.</summary>
    private void AddDeviceChoices(List<Choice> items, string current)
    {
        List<AudioDeviceInfo> inputs, outputs;
        try { inputs = WasapiSource.ListDevices(true); } catch { inputs = new(); }
        try { outputs = WasapiSource.ListDevices(false); } catch { outputs = new(); }
        if (inputs.Count > 0)
        {
            items.Add(new Choice("Audio inputs", "", true));
            items.AddRange(inputs.Select(d => new Choice("Input: " + d.Name, "in:" + d.Id)));
        }
        if (outputs.Count > 0)
        {
            items.Add(new Choice("Outputs (whatever plays on them)", "", true));
            items.AddRange(outputs.Select(d => new Choice("Output: " + d.Name, "out:" + d.Id)));
        }
        if (IsDeviceValue(current) && !items.Any(i => Same(i.Value, current)))
            items.Add(new Choice("Saved device (not connected)", current));
    }

    private void UpdateMonitorLabel()
    {
        var m = _app.SelectedMonitor;
        if (m == null) { MonitorLabel.Text = "No monitor"; return; }
        MonitorLabel.Text = $"{(string.IsNullOrEmpty(m.FriendlyName) ? m.ShortName : m.FriendlyName)}  ·  {m.Width}×{m.Height}{(m.IsPrimary ? "  ·  primary" : "")}";
        MonitorDetail.Text = m.IsHdr
            ? $"HDR on · tone-mapped to SDR (SDR white {m.SdrWhiteNits:0} nits) · {m.AdapterName}"
            : $"SDR · {m.AdapterName}";
    }

    // ------------------------------------------------------------------ status

    private void RefreshStatus()
    {
        if (!IsLoaded) return;
        var eng = _app.Engine;
        var state = _app.State;
        StatusDot.Fill = (Brush)FindResource(state switch
        {
            RecordingState.Recording => "Good",
            RecordingState.Error => "Danger",
            RecordingState.Paused => "Muted",
            _ => "Warn",
        });
        StatusText.Text = _app.StatusText;

        bool noticeShowing = DateTime.UtcNow < _noticeUntil;
        if (eng.IsRunning)
        {
            var st = eng.GetStats();
            string game = st.Game != null ? $" · {st.Game}" : "";
            if (!noticeShowing) FooterText.Text = $"{st.Encoder.Replace(" Encoder MFT", "")} · buffer {st.BufferedSeconds:0}s · {st.FramesOut} frames · {st.Dropped} dropped{game}";
            // Each entry says what the track is actually capturing ("● Game (Overwatch)", "● Game (everything except Discord)",
            // "● Mic (USB Microphone)") or why it is not ("○ Voice (waiting for app)"), so a pick is confirmed here and the
            // fallback is not mistaken for the real thing. Non-breaking spaces inside an entry: the line wraps between tracks only.
            AudioStatus.Text = st.Audio.Count == 0 ? "No audio tracks" :
                string.Join("   ", st.Audio.Select(a => $"{(a.Running ? "●" : "○")} {a.Name}{(a.Detail != null ? $" ({a.Detail})" : "")}".Replace(' ', ' ')));
        }
        else
        {
            if (!noticeShowing)
                FooterText.Text = state switch
                {
                    RecordingState.Error => eng.Error ?? "Error",
                    RecordingState.WaitingForGame => "Not recording until a game or custom app runs (Capture → Recording)",
                    RecordingState.Paused => "Recording paused",
                    _ => eng.Status,
                };
            // Never leave the previous run's "● Mix   ● Game" line under a Paused / Error / Starting header: with the pickers
            // right above it, that reads as "my new selection is active".
            AudioStatus.Text = state switch
            {
                RecordingState.Paused => "Paused",
                RecordingState.WaitingForGame => "Starts with the game",
                RecordingState.Error => "Not recording",
                _ => eng.Status,
            };
        }

        string? game2 = _app.CurrentGame;
        string source = _app.CurrentGameSource switch { "fullscreen" => "fullscreen window", "library" => "game library folder", "custom" => "custom app", _ => "" };
        ModeStatus.Text = _app.GameOnly
            ? game2 != null ? $"Detected: {game2}{(source.Length > 0 ? $" ({source})" : "")}" : "Waiting for a game… detected by a fullscreen window on the captured monitor, a known game-library folder (Steam, Epic, Riot, Battle.net, Ubisoft, EA, GOG, Xbox, Rockstar) or a custom app below."
            : game2 != null ? $"Recording whenever Clink runs · game: {game2}" : "Recording whenever Clink runs.";
    }

    // ------------------------------------------------------------------ updates

    private void RefreshUpdateState()
    {
        var v = _app.PendingVersion;
        UpdateButton.Content = v != null ? $"Update to v{v}" : "Update";
        UpdateButton.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowButton.Content = v != null ? $"Update to v{v}" : "Update";
        UpdateNowButton.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
        string? problem = _app.Updater.InstallProblem ?? (_app.Updater.LastError != null ? "Last check failed: " + _app.Updater.LastError : null);
        UpdateProblemText.Text = problem ?? "";
        UpdateProblemText.Visibility = problem != null ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdateButton.IsEnabled = _app.Updater.Repo != null;
        if (_app.Updater.Repo == null) UpdateVersionText.Text = $"Clink v{_app.Updater.VersionDisplay} · updates are not configured in this build";
    }

    private void Update_Click(object sender, RoutedEventArgs e) => _app.UpdateNow();

    private void AutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.AutoUpdate = AutoUpdateCheck.IsChecked == true;
        _app.Settings.Save();
    }

    private async void Version_Click(object sender, MouseButtonEventArgs e) => await CheckForUpdatesAsync();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        if (_checkingUpdate) return;
        var up = _app.Updater;
        if (up.Repo == null) { _app.Say("Updates are not configured in this build"); return; }
        _checkingUpdate = true;
        _app.Say("Checking…");
        try
        {
            var check = up.CheckNowAsync();
            // A newer release is downloaded inside the same call (~30 MB), and the call also waits for a download already in
            // progress: keep the footer honest instead of letting the stats line take it back after 4 s.
            while (await Task.WhenAny(check, Task.Delay(2000)) != check)
            {
                string text = up.IsDownloading ? "Downloading update…" : "Still checking…";
                if (_app.LastNotice != text) _app.Say(text); else _noticeUntil = DateTime.UtcNow.AddSeconds(4);
            }
            var info = await check;
            if (info == null) _app.Say(up.LastError != null ? "Update check failed: " + up.LastError : $"You're up to date (v{up.VersionDisplay})");
            else if (_app.PendingVersion is Version pending && pending >= info.Version) _app.Say($"Update v{info.Version} is ready - click 'Update to v{pending}' to restart");
            else if (!_app.Settings.AutoUpdate)
            {
                // Nothing is downloaded with auto-update off, so make the answer actionable: show the release.
                _app.Say($"Clink v{info.Version} is available (auto-update is off) - opening the release page");
                OpenUrl(string.IsNullOrEmpty(info.HtmlUrl) ? $"https://github.com/{up.Repo}/releases/latest" : info.HtmlUrl);
            }
            else _app.Say($"Update v{info.Version} available");
        }
        finally { _checkingUpdate = false; RefreshUpdateState(); }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _app.Say("Could not open the browser: " + ex.Message); }
    }

    // ------------------------------------------------------------------ clips

    internal void OpenTrim(ClipEntry entry)
    {
        var w = new TrimWindow(entry) { Owner = IsVisible ? this : null };
        w.Show();
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => _app.OpenFolder();

    private void ClipNow_Click(object sender, RoutedEventArgs e) => _app.ClipNow();

    // ------------------------------------------------------------------ settings handlers

    private void ScheduleRestart()
    {
        if (_loading) return;
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private void QualityChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _filling) return;
        var s = _app.Settings;
        s.Resolution = Selected<string>(ResolutionCombo) ?? "native";
        s.Fps = Selected<int>(FpsCombo) is var f && f > 0 ? f : 60;
        if (BitrateCombo.IsEnabled && Selected<int>(BitrateCombo) is var b && b > 0) s.BitrateMbps = b;   // read-only while a size target picks it
        s.Codec = Selected<string>(CodecCombo) ?? "h264";
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.Save();
        UpdateCodecDescription();
        UpdateEstimates();
        ScheduleRestart();
    }

    private void ReplayLengthCommitted(int seconds)
    {
        if (_loading) return;
        var s = _app.Settings;
        if (s.BufferSeconds != seconds)
        {
            s.BufferSeconds = seconds;
            s.Save();
            ScheduleRestart();
        }
        HotkeyEditor.DefaultSeconds = seconds;
        UpdateEstimates();
    }

    private void HotkeysChanged()
    {
        if (_loading) return;
        var s = _app.Settings;
        s.Hotkeys = HotkeyEditor.Actions.ToList();
        // Keep the v1 single binding in step with the first clip hotkey (the tray and balloon labels read ClipHotkeyLabel from the list).
        var clip = s.Hotkeys.FirstOrDefault(h => Hotkeys.IsClip(h.Action) && h.Keys.Vk > 0);
        if (clip != null) s.Hotkey = clip.Keys;
        s.Save();
        _app.ReloadHotkeys();
    }

    private void AudioChanged(object sender, RoutedEventArgs e)
    {
        UpdateAudioEnabled();
        if (_loading) return;
        DismissAudioSetupBanner();
        var s = _app.Settings;
        s.TrackDesktop = TrackDesktopCheck.IsChecked == true;
        s.TrackGame = TrackGameCheck.IsChecked == true;
        s.TrackMic = TrackMicCheck.IsChecked == true;
        s.TrackVoice = TrackVoiceCheck.IsChecked == true;
        s.Save();
        UpdateEstimates();   // the track count is part of the size estimate and of a size target's bitrate
        ScheduleRestart();
    }

    private void AudioSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
        DismissAudioSetupBanner();
        var s = _app.Settings;
        // Only the picker that changed writes its setting, so what another picker happens to show can never clobber a saved value.
        if (sender == AllDeviceCombo) s.AllDeviceId = DeviceId(AllDeviceCombo);
        else if (sender == GameCombo) s.GameProcess = ProcessName(GameCombo);
        else if (sender == MicCombo) s.MicDeviceId = DeviceId(MicCombo);
        else if (sender == VoiceCombo) s.VoiceApp = ProcessName(VoiceCombo);
        else return;
        s.Save();
        ScheduleRestart();
    }

    private static string? DeviceId(ComboBox combo) => Selected<string>(combo) is { Length: > 0 } id ? id : null;   // "" = the default device

    private static string ProcessName(ComboBox combo) => Selected<string>(combo) is { Length: > 0 } name ? name : "auto";

    // ------------------------------------------------------------------ "set up your audio" banner

    private void AudioGroup_MouseEnter(object sender, MouseEventArgs e) => DismissAudioSetupBanner();

    /// <summary>
    /// Hides the banner above the audio group for good: the user has looked at the group (hovered over it) or changed an option
    /// in it. <see cref="Settings.AudioSetupSeen"/> is saved so it never comes back; while the window is still loading only the
    /// visibility is touched.
    /// </summary>
    private void DismissAudioSetupBanner()
    {
        if (AudioSetupBanner == null || AudioSetupBanner.Visibility == Visibility.Collapsed) return;
        AudioSetupBanner.Visibility = Visibility.Collapsed;
        if (_loading) return;
        _app.Settings.AudioSetupSeen = true;
        _app.Settings.Save();
    }

    private void MiscChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.ClickSound = ClickSoundCheck.IsChecked == true;
        _app.Settings.Save();
        if (_app.Settings.ClickSound && sender == ClickSoundCheck) ClickSound.Play(_app.Settings.ClickVolume);
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.RunAtStartup = StartupCheck.IsChecked == true;
        _app.Settings.Save();
        _app.SetRunAtStartup(_app.Settings.RunAtStartup);
    }

    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TokenSaveButton.IsEnabled = true;
    }

    private void TokenSave_Click(object sender, RoutedEventArgs e)
    {
        string t = TokenBox.Password.Trim();
        _app.Settings.CopiumToken = string.IsNullOrEmpty(t) ? null : t;
        _app.Settings.Save();
        TokenSaveButton.IsEnabled = false;
        if (string.IsNullOrEmpty(t)) { CopiumStatus.Text = "Not connected"; CopiumStatus.Foreground = (Brush)FindResource("Muted"); }
        else if (!t.StartsWith("eup_")) { CopiumStatus.Text = "Saved (tokens usually start with eup_)"; CopiumStatus.Foreground = (Brush)FindResource("Warn"); }
        else { CopiumStatus.Text = "Token saved"; CopiumStatus.Foreground = (Brush)FindResource("Good"); }
    }

    private void CopiumSite_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://copium.pics/") { UseShellExecute = true }); } catch { }
    }

    private void CopiumPlusChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.CopiumPlus = CopiumPlusCheck.IsChecked == true;
        _app.Settings.Save();
    }

    // ------------------------------------------------------------------ toasts & status dot (§7)

    private void UpdateToastLabels()
    {
        if (ToastRecordingLabel == null) return;   // a slider's Minimum coerces its Value while the XAML is still loading
        ToastClipLabel.Text = $"{(int)ToastClipSlider.Value} s";
        ToastSharedLabel.Text = $"{(int)ToastSharedSlider.Value} s";
        ToastRecordingLabel.Text = $"{(int)ToastRecordingSlider.Value} s";
    }

    private void ToastChanged(object sender, RoutedEventArgs e)
    {
        UpdateToastLabels();
        if (_loading || _filling) return;
        var s = _app.Settings;
        s.ToastPosition = Selected<string>(ToastPositionCombo) ?? "bottom-right";
        s.ToastClip.Enabled = ToastClipCheck.IsChecked == true; s.ToastClip.Seconds = (int)ToastClipSlider.Value;
        s.ToastShared.Enabled = ToastSharedCheck.IsChecked == true; s.ToastShared.Seconds = (int)ToastSharedSlider.Value;
        s.ToastRecording.Enabled = ToastRecordingCheck.IsChecked == true; s.ToastRecording.Seconds = (int)ToastRecordingSlider.Value;
        ScheduleSave();   // sliders fire on every tick while dragging
    }

    private void ToastPreview_Click(object sender, RoutedEventArgs e)
    {
        _app.Toasts.Preview((sender as FrameworkElement)?.Tag as string ?? "clip");
    }

    private void DotChanged(object sender, RoutedEventArgs e)
    {
        if (DotPositionCombo == null || DotCheck == null) return;
        DotPositionCombo.IsEnabled = DotCheck.IsChecked == true;
        if (_loading || _filling) return;
        var s = _app.Settings;
        s.StatusDot = DotCheck.IsChecked == true;
        s.StatusDotPosition = Selected<string>(DotPositionCombo) ?? "top-right";
        s.Save();
        _app.ApplyStatusDot();
    }

    // ------------------------------------------------------------------ storage (§9)

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Enter commits like leaving the box does: move focus so LostFocus runs the handler.
        (sender as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void ClipsFolderBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        string path = ClipsFolderBox.Text.Trim().Trim('"');
        if (path.Length == 0 || string.Equals(path, s.ClipsFolder, StringComparison.OrdinalIgnoreCase)) { ClipsFolderBox.Text = s.ClipsFolder; return; }
        if (!string.IsNullOrWhiteSpace(s.ArchivePath) && ArchiveService.IsSameFolder(path, s.ArchivePath))
        {
            _app.Say("The clips folder cannot be the archive location");
            ClipsFolderBox.Text = s.ClipsFolder;
            return;
        }
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { _app.Say("Cannot use that folder: " + ex.Message); ClipsFolderBox.Text = s.ClipsFolder; return; }
        s.ClipsFolder = path;
        s.Save();
        _app.Library.Load(path);
        _app.Say("Clips folder changed");
        ScheduleRestart();   // the engine writes clips to its configured folder
    }

    private void BrowseClips_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder("Local clips folder", _app.Settings.ClipsFolder);
        if (picked == null) return;
        ClipsFolderBox.Text = picked;
        ClipsFolderBox_LostFocus(sender, e);
    }

    private void BrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        string? picked = PickFolder("Archive location (NAS share, mapped drive or folder)", _app.Settings.ArchivePath);
        if (picked == null) return;
        ArchivePathBox.Text = picked;
        ArchivePathBox_LostFocus(sender, e);
    }

    private string? PickFolder(string title, string? initial)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
            if (!string.IsNullOrWhiteSpace(initial)) { try { if (Directory.Exists(initial)) dlg.InitialDirectory = initial; } catch { } }
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }
        catch (Exception ex) { _app.Say("Folder picker failed: " + ex.Message); return null; }
    }

    private void UpdateArchiveEnabled()
    {
        if (ArchivePanel == null || ArchiveCheck == null) return;
        ArchivePanel.IsEnabled = ArchiveCheck.IsChecked == true;
        ArchivePanel.Opacity = ArchiveCheck.IsChecked == true ? 1 : 0.55;
    }

    private void ArchiveChanged(object sender, RoutedEventArgs e)
    {
        UpdateArchiveEnabled();
        if (_loading) return;
        _app.Settings.ArchiveEnabled = ArchiveCheck.IsChecked == true;
        _app.Settings.Save();
        _app.Archive.Reevaluate();   // the status line follows the switch now, not at the next 5-minute run
        RefreshArchiveStatus();
    }

    private void ArchivePathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        string path = ArchivePathBox.Text.Trim().Trim('"');
        string? value = path.Length == 0 ? null : path;
        if (string.Equals(value, s.ArchivePath, StringComparison.OrdinalIgnoreCase)) return;
        if (value != null && ArchiveService.IsSameFolder(value, s.ClipsFolder))
        {
            // Moving a clip onto itself would be "copy verified, delete local": the one copy gone. Refused here and in the service.
            _app.Say("The archive location cannot be the clips folder itself");
            ArchiveStatus.Text = "Archive location is the clips folder: pick another folder, share or drive";
            ArchiveStatus.Foreground = (Brush)FindResource("Warn");
            ArchivePathBox.Text = s.ArchivePath ?? "";
            return;
        }
        s.ArchivePath = value;
        s.Save();
        _app.Archive.Reevaluate();
        RefreshArchiveStatus();
    }

    private void ArchiveRulesChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        if (int.TryParse(ArchiveDaysBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days >= 0) s.ArchiveAfterDays = days;
        ArchiveDaysBox.Text = s.ArchiveAfterDays.ToString(CultureInfo.InvariantCulture);
        string gbText = ArchiveGbBox.Text.Trim().Replace(',', '.');
        if (double.TryParse(gbText, NumberStyles.Float, CultureInfo.InvariantCulture, out double gb) && gb >= 0) s.ArchiveWhenLocalExceedsGB = gb;
        ArchiveGbBox.Text = s.ArchiveWhenLocalExceedsGB.ToString("0.##", CultureInfo.InvariantCulture);
        s.Save();
        _app.Archive.Reevaluate();
        RefreshArchiveStatus();
    }

    private async void ArchiveTest_Click(object sender, RoutedEventArgs e)
    {
        ArchivePathBox_LostFocus(sender, e);
        string path = ArchivePathBox.Text.Trim().Trim('"');
        ArchiveTestButton.IsEnabled = false;
        ArchiveStatus.Text = "Testing…";
        ArchiveStatus.Foreground = (Brush)FindResource("Muted");
        try
        {
            var (ok, message, _) = await _app.Archive.TestAsync(path);
            ArchiveStatus.Text = message;
            ArchiveStatus.Foreground = (Brush)FindResource(ok ? "Good" : "Warn");
        }
        finally { ArchiveTestButton.IsEnabled = true; }
    }

    private async void ArchiveNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_app.Settings.ArchiveEnabled || string.IsNullOrWhiteSpace(_app.Settings.ArchivePath)) { _app.Say("Turn the archive on and set its location first"); return; }
        ArchiveNowButton.IsEnabled = false;
        try { await _app.Archive.RunNowAsync(); }
        finally { ArchiveNowButton.IsEnabled = true; RefreshArchiveStatus(); }
    }

    private void RefreshArchiveStatus()
    {
        if (!IsLoaded) return;
        ArchiveStatus.Text = _app.Archive.Status;
        string t = ArchiveStatus.Text;
        ArchiveStatus.Foreground = (Brush)FindResource(t.Contains("unreachable") || t.Contains("denied") || t.Contains("no rule") ? "Warn" : "Muted");
    }

    // ------------------------------------------------------------------ window chrome

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) { HotkeyEditor.CancelCapture(); WindowState = WindowState.Minimized; }

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    public void HideToTray()
    {
        HotkeyEditor.CancelCapture();
        if (WindowState == WindowState.Normal)
        {
            _app.Settings.WindowX = Left; _app.Settings.WindowY = Top;
            _app.Settings.WindowWidth = ActualWidth; _app.Settings.WindowHeight = ActualHeight;
        }
        _saveTimer.Stop();
        _app.Settings.Save();
        Hide();
        if (!_hintShown)
        {
            _hintShown = true;
            string how = _app.ClipHotkeyLabel is string key ? $"Press {key} to save a clip." : "Add a hotkey under Capture → Hotkeys to save clips.";
            _app.ShowBalloon("Clink is still running", how + " Right-click the tray icon to quit.");
        }
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }
}
