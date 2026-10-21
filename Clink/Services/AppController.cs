using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Clink.Audio;
using Clink.Engine;
using Clink.Native;

namespace Clink.Services;

/// <summary>Owns the engine, settings, clip library, hotkey, tray icon and the single-instance pipe.</summary>
internal sealed class AppController : IDisposable
{
    public const string PipeName = "Clink.Control";
    private const string MutexName = "Clink.SingleInstance.6F1B";

    public static AppController Instance { get; private set; } = null!;

    public Settings Settings { get; }
    public ClipLibrary Library { get; } = new();
    public Engine.Engine Engine { get; } = new();
    public Updater Updater { get; }
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public bool IsPaused { get; private set; }
    public string LastNotice { get; private set; } = "";
    /// <summary>Version of a downloaded update waiting to be installed, or null.</summary>
    public Version? PendingVersion => Updater.PendingVersion;

    public event Action? StateChanged;
    public event Action<string>? Notice;
    public event Action<ClipEntry>? ClipSaved;
    public event Action? ShowRequested;
    /// <summary>"Clink.exe --trim-last" (or the pipe command "trim"): open the trim editor on the newest clip.</summary>
    public event Action? TrimLastRequested;
    /// <summary>An update became ready to install (or the pending state changed otherwise). Raised on the UI thread.</summary>
    public event Action? UpdateStateChanged;

    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _pauseItem;
    private System.Windows.Forms.ToolStripMenuItem? _updateItem;
    private DispatcherTimer? _timer;
    private static Mutex? s_mutex;
    private int _saving;
    private DateTime _lastClipAt = DateTime.MinValue;

    public AppController(Settings settings, Updater updater)
    {
        Settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        Instance = this;
        Updater = updater;
        Updater.UpdateAvailable += info => _ui.BeginInvoke(() => OnUpdateAvailable(info));
        Updater.UpdateReady += version => _ui.BeginInvoke(() => OnUpdateReady(version));
    }

    // ------------------------------------------------------------------ single instance

    /// <summary>Returns true if this process should run; otherwise the command was forwarded to the running instance.</summary>
    public static bool AcquireSingleInstance(string[] args)
    {
        s_mutex = new Mutex(true, MutexName, out bool createdNew);
        if (createdNew) return true;
        string cmd = args.Any(a => a.Equals("--clip", StringComparison.OrdinalIgnoreCase)) ? "clip"
            : args.Any(a => a.Equals("--trim-last", StringComparison.OrdinalIgnoreCase)) ? "trim"
            : "show";
        SendCommand(cmd);
        return false;
    }

    /// <summary>Gives up the single-instance mutex so a replacement process can start (used when handing over to an update).</summary>
    public static void ReleaseSingleInstance()
    {
        try { s_mutex?.Dispose(); } catch { }
        s_mutex = null;
    }

    public static void SendCommand(string cmd)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            w.WriteLine(cmd);
            w.Flush();
        }
        catch (Exception ex) { Log.Warn("pipe send: " + ex.Message); }
    }

    private async Task PipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var r = new StreamReader(server);
                string? line = await r.ReadLineAsync(ct);
                switch (line?.Trim().ToLowerInvariant())
                {
                    case "clip": _ui.BeginInvoke(ClipNow); break;
                    case "show": _ui.BeginInvoke(() => ShowRequested?.Invoke()); break;
                    case "trim": _ui.BeginInvoke(() => TrimLastRequested?.Invoke()); break;
                    case "pause": _ui.BeginInvoke(() => { if (!IsPaused) TogglePause(); }); break;
                    case "resume": _ui.BeginInvoke(() => { if (IsPaused) TogglePause(); }); break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("pipe server: " + ex.Message); await Task.Delay(500, ct).ContinueWith(_ => { }); }
        }
    }

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        Monitors = Native.Monitors.Enumerate();
        Library.Load(Settings.ClipsFolder);
        Hotkeys.Install(Settings.Hotkey, () => _ui.BeginInvoke(ClipNow));
        Engine.StateChanged += () => _ui.BeginInvoke(() => StateChanged?.Invoke());
        _ = PipeServerAsync(_cts.Token);
        CreateTray();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => StateChanged?.Invoke(), _ui);
        _timer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (Settings.RunAtStartup) SetRunAtStartup(true); // keep the Run entry pointing at wherever the exe lives now
        StartEngine();
        Updater.StartBackgroundChecks();
        // A downloaded update was skipped at startup (install failed earlier, read-only folder, elevated): tell the user, once per start.
        if (Updater.InstallProblem is string problem) { Say(problem); ShowBalloon("Clink update not installed", problem); }
    }

    // ------------------------------------------------------------------ updates

    private static bool MainWindowVisible => System.Windows.Application.Current?.MainWindow?.IsVisible == true;

    private void OnUpdateAvailable(UpdateInfo info)
    {
        // With auto-update off this only happens from a manual check, and the version click opens the release page itself.
        Log.Info($"update: v{info.Version} ({info.Tag}) available{(Settings.AutoUpdate ? "" : " (auto-update is off)")}");
    }

    private void OnUpdateReady(Version version)
    {
        if (_updateItem != null) { _updateItem.Text = $"Update to v{version}"; _updateItem.Visible = true; }
        string? problem = Updater.InstallProblemFor(Environment.ProcessPath ?? "");
        string msg = problem == null
            ? $"v{version} downloaded - installs on next start, or click 'Update to v{version}'"
            : $"v{version} downloaded, but {problem}. Install it by hand from {Updater.UpdatesDir}";
        Say(msg);
        if (!MainWindowVisible) ShowBalloon("Clink update ready", msg);
        UpdateStateChanged?.Invoke();
    }

    /// <summary>Installs the downloaded update now: hands over to the new exe and quits.</summary>
    public void UpdateNow()
    {
        string? pending = Updater.PendingPath;
        if (pending == null || !File.Exists(pending)) { Say("No update downloaded yet"); return; }
        string? problem = Updater.InstallProblemFor(Environment.ProcessPath ?? "");
        if (problem != null) { Say($"Cannot install v{Updater.PendingVersion}: {problem}. Install it by hand from {Updater.UpdatesDir}"); return; }
        try
        {
            Log.Info($"update: installing v{Updater.PendingVersion} from {pending}");
            // Come back the way we are now: visible if the window is open, otherwise in the tray.
            Updater.LaunchApply(pending, Environment.ProcessPath!, new[] { MainWindowVisible ? "--show" : "--minimized" });
        }
        catch (Exception ex)
        {
            Log.Error("update: launch failed", ex);
            Say("Update failed to start: " + ex.Message);
            return;
        }
        Quit();
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke(async () =>
        {
            await Task.Delay(1500);
            Monitors = Native.Monitors.Enumerate();
            Say("Display changed, restarting capture");
            StartEngine();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _ui.BeginInvoke(async () => { await Task.Delay(3000); StartEngine(); });
    }

    public MonitorInfo? SelectedMonitor =>
        Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, Settings.MonitorDevice, StringComparison.OrdinalIgnoreCase))
        ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();

    public EngineConfig BuildConfig()
    {
        var mon = SelectedMonitor ?? throw new InvalidOperationException("No monitor found");
        (uint w, uint h) = OutputSize(mon, Settings.Resolution);
        return new EngineConfig
        {
            Monitor = mon,
            OutWidth = w,
            OutHeight = h,
            Fps = (uint)Math.Clamp(Settings.Fps, 15, 120),
            BitrateMbps = (uint)Math.Clamp(Settings.BitrateMbps, 2, 200),
            // The combos match settings case-insensitively (a hand-edited settings.json still selects), so the engine must too.
            Hevc = string.Equals(Settings.Codec, "hevc", StringComparison.OrdinalIgnoreCase),
            GopSeconds = Math.Clamp(Settings.KeyframeSeconds, 0.25f, 5f),
            BufferSeconds = Math.Clamp(Settings.BufferSeconds, 5, 1200),
            CaptureCursor = Settings.CaptureCursor,
            TrackDesktop = Settings.TrackDesktop, TrackGame = Settings.TrackGame, TrackMic = Settings.TrackMic, TrackVoice = Settings.TrackVoice,
            AllDeviceId = Settings.AllDeviceId,
            MicDeviceId = Settings.MicDeviceId,
            GameProcess = GameProcessTracker.NormalizeName(Settings.GameProcess),
            VoiceApp = GameProcessTracker.NormalizeName(Settings.VoiceApp),
            HdrKnee = Settings.HdrKnee, HdrPeak = Settings.HdrPeak,
            ClipsFolder = Settings.ClipsFolder,
        };
    }

    public static (uint w, uint h) OutputSize(MonitorInfo mon, string resolution)
    {
        int targetH = resolution.Trim().ToLowerInvariant() switch { "1440p" => 1440, "1080p" => 1080, "720p" => 720, _ => 0 };
        double w = mon.Width, h = mon.Height;
        if (targetH > 0 && targetH < h) { w = w * targetH / h; h = targetH; }
        return ((uint)Math.Round(w / 2) * 2, (uint)Math.Round(h / 2) * 2);
    }

    private int _startGen;

    public void StartEngine()
    {
        if (IsPaused) return;
        int gen = Interlocked.Increment(ref _startGen);
        EngineConfig cfg;
        try { cfg = BuildConfig(); }
        catch (Exception ex) { Say("Cannot start: " + ex.Message); return; }
        Task.Run(() =>
        {
            if (gen != _startGen) return;
            try { Engine.Start(cfg); }
            catch (Exception ex) { Log.Error("Engine start", ex); }
            _ui.BeginInvoke(() =>
            {
                if (!Engine.IsRunning && Engine.Error != null) Say("Recording failed: " + Engine.Error);
                StateChanged?.Invoke();
            });
        });
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        if (IsPaused) { Task.Run(() => Engine.Stop()); Say("Recording paused"); }
        else { Say("Recording resumed"); StartEngine(); }
        if (_pauseItem != null) _pauseItem.Text = IsPaused ? "Resume recording" : "Pause recording";
        StateChanged?.Invoke();
    }

    public void Say(string msg)
    {
        LastNotice = msg;
        Log.Info("notice: " + msg);
        Notice?.Invoke(msg);
    }

    // ------------------------------------------------------------------ clips

    public async void ClipNow()
    {
        if (!Engine.IsRunning) { Say(IsPaused ? "Recording is paused" : "Not recording yet"); return; }
        if ((DateTime.UtcNow - _lastClipAt).TotalMilliseconds < 700) return;
        if (Interlocked.Exchange(ref _saving, 1) == 1) return;
        _lastClipAt = DateTime.UtcNow;
        if (Settings.ClickSound) ClickSound.Play(Settings.ClickVolume);
        try
        {
            var result = await Engine.SaveClipAsync();
            var tracks = Engine.GetStats().Audio.Select(a => a.Name).ToArray();
            var entry = Library.Add(result.Path, result.DurationSeconds, result.SizeBytes, result.Game, tracks);
            Say($"Clip saved ({result.DurationSeconds:0}s)");
            ClipSaved?.Invoke(entry);
        }
        catch (Exception ex)
        {
            Log.Error("Save clip", ex);
            Say("Clip failed: " + ex.Message);
        }
        finally { _saving = 0; }
    }

    public async Task<bool> ShareAsync(ClipEntry entry, IProgress<double>? progress = null)
    {
        if (!string.IsNullOrEmpty(entry.CopiumLink))
        {
            CopyToClipboard(entry.CopiumLink);
            Say("Link copied");
            return true;
        }
        string? token = Settings.CopiumToken;
        if (string.IsNullOrWhiteSpace(token)) { Say("Add your Copium token first"); ShowRequested?.Invoke(); return false; }
        try
        {
            Say("Uploading to Copium…");
            string title = (entry.Game ?? "Clip") + " " + entry.Created.ToString("yyyy-MM-dd HH:mm");
            var up = await CopiumClient.UploadAsync(token, entry.Path, title, progress, _cts.Token);
            entry.CopiumLink = up.Link;
            entry.CopiumPageUrl = up.PageUrl;
            entry.CopiumId = up.Id;
            Library.Update(entry);
            CopyToClipboard(up.Link);
            Say("Uploaded, link copied");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Copium upload", ex);
            Say("Upload failed: " + ex.Message);
            return false;
        }
    }

    public void CopyToClipboard(string text)
    {
        try { _ui.Invoke(() => Clipboard.SetDataObject(text, true)); }
        catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); }
    }

    public void OpenFolder(string? file = null)
    {
        try
        {
            if (file != null && File.Exists(file)) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else { Directory.CreateDirectory(Settings.ClipsFolder); Process.Start("explorer.exe", $"\"{Settings.ClipsFolder}\""); }
        }
        catch (Exception ex) { Say("Could not open folder: " + ex.Message); }
    }

    public void SaveSettings() => Settings.Save();

    public void SetRunAtStartup(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("Clink", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("Clink", false);
        }
        catch (Exception ex) { Say("Startup setting failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ tray

    private void CreateTray()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add($"Clip now ({Settings.Hotkey})", null, (_, _) => _ui.BeginInvoke(ClipNow));
            menu.Items.Add("Show Clink", null, (_, _) => ShowRequested?.Invoke());
            _pauseItem = new System.Windows.Forms.ToolStripMenuItem("Pause recording", null, (_, _) => TogglePause());
            menu.Items.Add(_pauseItem);
            menu.Items.Add("Open clips folder", null, (_, _) => OpenFolder());
            _updateItem = new System.Windows.Forms.ToolStripMenuItem("Update Clink", null, (_, _) => UpdateNow()) { Visible = false };
            menu.Items.Add(_updateItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Clink",
                Visible = true,
                ContextMenuStrip = menu,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowRequested?.Invoke(); };
        }
        catch (Exception ex) { Log.Warn("tray: " + ex.Message); }
    }

    public void UpdateTrayHotkey() { if (_tray?.ContextMenuStrip?.Items.Count > 0) _tray.ContextMenuStrip.Items[0].Text = $"Clip now ({Settings.Hotkey})"; }

    public void ShowBalloon(string title, string text)
    {
        if (text.Length > 250) text = text[..249] + "…";   // Shell_NotifyIcon cuts balloon text at 256 characters
        try { _tray?.ShowBalloonTip(3000, title, text, System.Windows.Forms.ToolTipIcon.None); } catch { }
    }

    public void Quit()
    {
        _ui.BeginInvoke(() =>
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer?.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Hotkeys.Uninstall();
        try { Updater.Dispose(); } catch { }
        try { Engine.Dispose(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        ReleaseSingleInstance();
    }
}
