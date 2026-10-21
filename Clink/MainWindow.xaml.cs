using System.Collections.ObjectModel;
using System.Diagnostics;
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

public partial class MainWindow : Window
{
    private readonly AppController _app = AppController.Instance;
    private readonly ObservableCollection<ClipItem> _clips = new();
    private readonly DispatcherTimer _restartTimer;
    private bool _loading = true;
    private bool _filling;                              // a source combo is being (re)populated: its SelectionChanged is not a user choice
    private bool _hintShown;
    private bool _tokenDirty;
    private bool _checkingUpdate;
    private DateTime _noticeUntil = DateTime.MinValue;   // keep a Say() notice visible this long before the stats line takes the footer back
    private ToastWindow? _toast;

    private sealed record Choice(string Label, object Value, bool Header = false) { public override string ToString() => Label; }

    public MainWindow()
    {
        InitializeComponent();
        _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _restartTimer.Tick += (_, _) => { _restartTimer.Stop(); _app.StartEngine(); };
        ClipsList.ItemsSource = _clips;

        var s = _app.Settings;
        if (s.WindowX is double wx && s.WindowY is double wy && !double.IsNaN(wx) && !double.IsNaN(wy))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = wx; Top = wy;
        }
        Loaded += (_, _) => Populate();
        Closing += (_, e) => { e.Cancel = true; HideToTray(); };
        LocationChanged += (_, _) => { if (!_loading && IsVisible) { s.WindowX = Left; s.WindowY = Top; } };

        _app.StateChanged += RefreshStatus;
        _app.Notice += msg => Dispatcher.BeginInvoke(() => { FooterText.Text = msg; _noticeUntil = DateTime.UtcNow.AddSeconds(4); });
        _app.ClipSaved += entry => Dispatcher.BeginInvoke(() => OnClipSaved(entry));
        _app.ShowRequested += () => Dispatcher.BeginInvoke(ShowFromTray);
        _app.TrimLastRequested += () => Dispatcher.BeginInvoke(() =>
        {
            var newest = _app.Library.Entries.FirstOrDefault(e => e.Exists);
            if (newest != null) OpenTrim(newest); else _app.Say("No clips to trim yet");
        });
        _app.Library.Changed += () => Dispatcher.BeginInvoke(RefreshClips);
        _app.UpdateStateChanged += () => Dispatcher.BeginInvoke(RefreshUpdateState);
    }

    // ------------------------------------------------------------------ populate

    private void Populate()
    {
        var s = _app.Settings;
        MonitorMap.SetMonitors(_app.Monitors, _app.SelectedMonitor?.DeviceName);
        MonitorMap.Selected += m => { s.MonitorDevice = m.DeviceName; s.Save(); UpdateMonitorLabel(); ScheduleRestart(); };
        UpdateMonitorLabel();

        Fill(ResolutionCombo, new[] { new Choice("Native", "native"), new Choice("1440p", "1440p"), new Choice("1080p", "1080p"), new Choice("720p", "720p") }, s.Resolution);
        Fill(FpsCombo, new[] { new Choice("30", 30), new Choice("60", 60), new Choice("120", 120) }, s.Fps);
        Fill(BitrateCombo, new[] { 8, 12, 16, 20, 25, 30, 40, 50, 60, 80 }.Select(b => new Choice($"{b} Mbps", b)).ToArray(), s.BitrateMbps);
        Fill(CodecCombo, new[] { new Choice("H.264", "h264"), new Choice("HEVC", "hevc") }, s.Codec);
        Fill(BufferCombo, new[] { 15, 30, 60, 90, 120, 180, 300, 600 }.Select(b => new Choice(b < 60 ? $"{b} s" : (b % 60 == 0 ? $"{b / 60} min" : $"{b / 60}:{b % 60:00} min"), b)).ToArray(), s.BufferSeconds);

        HotkeyButton.Content = s.Hotkey.ToString();
        ClickSoundCheck.IsChecked = s.ClickSound;
        CursorCheck.IsChecked = s.CaptureCursor;
        StartupCheck.IsChecked = s.RunAtStartup;
        TrackDesktopCheck.IsChecked = s.TrackDesktop;
        TrackGameCheck.IsChecked = s.TrackGame;
        TrackMicCheck.IsChecked = s.TrackMic;
        TrackVoiceCheck.IsChecked = s.TrackVoice;

        FillDeviceCombo(AllDeviceCombo, false, s.AllDeviceId);
        FillGameCombo(s.GameProcess);
        FillDeviceCombo(MicCombo, true, s.MicDeviceId);
        FillVoiceCombo(s.VoiceApp);
        UpdateAudioEnabled();

        if (!string.IsNullOrEmpty(s.CopiumToken)) { TokenBox.Password = s.CopiumToken; CopiumStatus.Text = "Token saved"; CopiumStatus.Foreground = (Brush)FindResource("Good"); }
        _tokenDirty = false;
        TokenSaveButton.IsEnabled = false;

        VersionText.Text = "v" + _app.Updater.VersionDisplay;
        VersionText.ToolTip = _app.Updater.Repo is string repo
            ? $"Updates come from github.com/{repo}{(_app.Updater.RepoOverridden ? " (UpdateRepo in settings.json)" : "")} · click to check now"
            : "Updates are not configured in this build";
        RefreshUpdateState();

        RefreshClips();
        RefreshStatus();
        _loading = false;
    }

    private static void Fill(ComboBox combo, Choice[] choices, object current)
    {
        combo.ItemsSource = choices;
        combo.SelectedItem = choices.FirstOrDefault(c => Same(c.Value, current)) ?? choices[0];
    }

    // string values (device ids, process names) compare case-insensitively so a hand-edited settings.json still matches
    private static bool Same(object a, object b) => a is string sa && b is string sb ? string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase) : Equals(a, b);

    private static T? Selected<T>(ComboBox combo) => combo.SelectedItem is Choice c && c.Value is T v ? v : default;

    // ------------------------------------------------------------------ audio sources

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

    private void Refill(ComboBox combo, List<Choice> items, string current)
    {
        bool was = _filling;
        _filling = true;
        try { Fill(combo, items.ToArray(), current); }
        finally { _filling = was; }
    }

    // The device and process lists are refreshed every time a dropdown opens (headsets get plugged in, games get launched while
    // the window sits in the tray); the current selection is always kept (see FillDeviceCombo / FillGameCombo).
    private void AllDeviceCombo_DropDownOpened(object sender, EventArgs e) => FillDeviceCombo(AllDeviceCombo, false, Selected<string>(AllDeviceCombo) ?? _app.Settings.AllDeviceId);

    private void GameCombo_DropDownOpened(object sender, EventArgs e) => FillGameCombo(Selected<string>(GameCombo) ?? _app.Settings.GameProcess);

    private void MicCombo_DropDownOpened(object sender, EventArgs e) => FillDeviceCombo(MicCombo, true, Selected<string>(MicCombo) ?? _app.Settings.MicDeviceId);

    private void VoiceCombo_DropDownOpened(object sender, EventArgs e) => FillVoiceCombo(Selected<string>(VoiceCombo) ?? _app.Settings.VoiceApp);

    private void UpdateAudioEnabled()
    {
        // Game, mic and voice are always captured for the mix, so their pickers stay live; only the optional Desktop track's does not.
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
        if (_app.IsPaused) { StatusDot.Fill = (Brush)FindResource("Warn"); StatusText.Text = "Paused"; }
        else if (eng.IsRunning) { StatusDot.Fill = (Brush)FindResource("Good"); StatusText.Text = "Recording"; }
        else if (eng.Error != null) { StatusDot.Fill = (Brush)FindResource("Danger"); StatusText.Text = "Error"; }
        else { StatusDot.Fill = (Brush)FindResource("Muted"); StatusText.Text = eng.Status; }

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
                string.Join("   ", st.Audio.Select(a => $"{(a.Running ? "●" : "○")} {a.Name}{(a.Detail != null ? $" ({a.Detail})" : "")}".Replace(' ', ' ')));
        }
        else
        {
            if (eng.Error != null && !noticeShowing) FooterText.Text = eng.Error;
            // Never leave the previous run's "● All audio   ● Game" line under a Paused / Error / Starting header: with the pickers
            // right above it, that reads as "my new selection is active".
            AudioStatus.Text = _app.IsPaused ? "Paused" : eng.Error != null ? "Not recording" : eng.Status;
        }
    }

    // ------------------------------------------------------------------ updates

    private void RefreshUpdateState()
    {
        var v = _app.PendingVersion;
        UpdateButton.Content = v != null ? $"Update to v{v}" : "Update";
        UpdateButton.Visibility = v != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Update_Click(object sender, RoutedEventArgs e) => _app.UpdateNow();

    private async void Version_Click(object sender, MouseButtonEventArgs e)
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
        finally { _checkingUpdate = false; }
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _app.Say("Could not open the browser: " + ex.Message); }
    }

    // ------------------------------------------------------------------ clips list

    private void RefreshClips()
    {
        var entries = _app.Library.Entries.Take(30).ToList();
        _clips.Clear();
        foreach (var e in entries) _clips.Add(new ClipItem(e));
        NoClipsText.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var items = _clips.ToList();
        Task.Run(() =>
        {
            foreach (var item in items)
            {
                var bmp = Thumbnails.Get(item.Entry.Path, 176, 100);
                if (bmp != null) Dispatcher.BeginInvoke(() => item.Thumbnail = bmp);
            }
        });
    }

    private void OnClipSaved(ClipEntry entry)
    {
        var s = _app.Settings;
        if (!s.ShowToast) return;
        _toast ??= new ToastWindow();
        _toast.Show(entry);
    }

    private static ClipItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item == null) return;
        await ShareItemAsync(item);
    }

    internal async Task ShareItemAsync(ClipItem item)
    {
        if (item.IsUploading) return;
        item.IsUploading = !item.HasLink;
        item.Status = item.HasLink ? null : "Uploading…";
        var progress = new Progress<double>(p => { item.Progress = p; item.Status = $"Uploading… {p:P0}"; });
        bool ok = await _app.ShareAsync(item.Entry, progress);
        item.IsUploading = false;
        item.Status = ok ? "Link copied to clipboard" : "Upload failed";
        item.Refresh();
        if (ok) _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (item.Status?.StartsWith("Link") == true) item.Status = null; }));
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item == null) return;
        OpenTrim(item.Entry);
    }

    internal void OpenTrim(ClipEntry entry)
    {
        var w = new TrimWindow(entry) { Owner = IsVisible ? this : null };
        w.Show();
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item != null) _app.OpenFolder(item.Entry.Path);
    }

    private void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        var item = ItemOf(sender);
        if (item == null) return;
        try { Process.Start(new ProcessStartInfo(item.Entry.Path) { UseShellExecute = true }); } catch (Exception ex) { _app.Say("Open failed: " + ex.Message); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => _app.OpenFolder();

    // ------------------------------------------------------------------ settings handlers

    private void ScheduleRestart()
    {
        if (_loading) return;
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private void QualityChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Settings;
        s.Resolution = Selected<string>(ResolutionCombo) ?? "native";
        s.Fps = Selected<int>(FpsCombo) is var f && f > 0 ? f : 60;
        s.BitrateMbps = Selected<int>(BitrateCombo) is var b && b > 0 ? b : 25;
        s.Codec = Selected<string>(CodecCombo) ?? "h264";
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.Save();
        ScheduleRestart();
    }

    private void BufferChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _app.Settings.BufferSeconds = Selected<int>(BufferCombo) is var b && b > 0 ? b : 120;
        _app.Settings.Save();
        ScheduleRestart();
    }

    private void AudioChanged(object sender, RoutedEventArgs e)
    {
        UpdateAudioEnabled();
        if (_loading) return;
        var s = _app.Settings;
        s.TrackDesktop = TrackDesktopCheck.IsChecked == true;
        s.TrackGame = TrackGameCheck.IsChecked == true;
        s.TrackMic = TrackMicCheck.IsChecked == true;
        s.TrackVoice = TrackVoiceCheck.IsChecked == true;
        s.Save();
        ScheduleRestart();
    }

    private void AudioSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _filling) return;
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

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        HotkeyButton.Content = "Press keys…";
        Hotkeys.CaptureNext((vk, ctrl, alt, shift, win) => Dispatcher.BeginInvoke(() =>
        {
            if (vk == 0x1B) { HotkeyButton.Content = _app.Settings.Hotkey.ToString(); return; } // Esc cancels
            var b = new HotkeyBinding { Vk = vk, Ctrl = ctrl, Alt = alt, Shift = shift, Win = win };
            _app.Settings.Hotkey = b;
            _app.Settings.Save();
            Hotkeys.Rebind(b);
            _app.UpdateTrayHotkey();
            HotkeyButton.Content = b.ToString();
        }));
    }

    private void ClipNow_Click(object sender, RoutedEventArgs e) => _app.ClipNow();

    private void Token_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _tokenDirty = true;
        TokenSaveButton.IsEnabled = true;
    }

    private void TokenSave_Click(object sender, RoutedEventArgs e)
    {
        string t = TokenBox.Password.Trim();
        _app.Settings.CopiumToken = string.IsNullOrEmpty(t) ? null : t;
        _app.Settings.Save();
        _tokenDirty = false;
        TokenSaveButton.IsEnabled = false;
        if (string.IsNullOrEmpty(t)) { CopiumStatus.Text = "Not connected"; CopiumStatus.Foreground = (Brush)FindResource("Muted"); }
        else if (!t.StartsWith("eup_")) { CopiumStatus.Text = "Saved (tokens usually start with eup_)"; CopiumStatus.Foreground = (Brush)FindResource("Warn"); }
        else { CopiumStatus.Text = "Token saved"; CopiumStatus.Foreground = (Brush)FindResource("Good"); }
    }

    private void CopiumSite_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://copium.pics/") { UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------ window chrome

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    public void HideToTray()
    {
        _app.Settings.WindowX = Left; _app.Settings.WindowY = Top;
        _app.Settings.Save();
        Hide();
        if (!_hintShown)
        {
            _hintShown = true;
            _app.ShowBalloon("Clink is still recording", $"Press {_app.Settings.Hotkey} to save a clip. Right-click the tray icon to quit.");
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
