using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Clink.Native;
using Clink.Screenshots;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The toast service (docs/SPEC-v2.md §7): "Clip saved", "Shared" and "Recording started" toasts (plus the share-last hotkey's
/// "Uploading" / "Clip not uploaded" notices, under the "Shared" switch), each with its own enable switch and duration
/// (<see cref="Settings.ToastClip"/> etc.), at one of 8 positions on the captured monitor's work area (<see cref="Settings.ToastPosition"/>, 16 px margin). Several toasts stack, the newest nearest the chosen edge; each fades in
/// (180 ms) and fades out (260 ms) before it closes, and the rest re-stack once it has. All methods may be called from any thread.
/// </summary>
internal sealed class Toasts : IScreenshotToasts
{
    private const int Margin = 16, Gap = 8;

    private readonly Settings _settings;
    private readonly Func<MonitorInfo?> _monitor;
    private readonly Dispatcher _ui;
    private readonly List<ToastWindow> _open = new();     // oldest first
    private ClipEntry? _lastClip;
    private DateTime _lastClipAt;

    /// <summary>Create on the UI thread (the toasts live on its dispatcher). <paramref name="monitor"/> returns the captured monitor.</summary>
    public Toasts(Settings settings, Func<MonitorInfo?> monitor)
    {
        _settings = settings;
        _monitor = monitor;
        _ui = Dispatcher.CurrentDispatcher;
    }

    public void ShowClipSaved(ClipEntry entry) => Post(() =>
    {
        if (!_settings.ToastClip.Enabled) return;
        // The v1 window and the controller may both report the same save during the transition: one toast per clip.
        if (ReferenceEquals(entry, _lastClip) && DateTime.UtcNow - _lastClipAt < TimeSpan.FromSeconds(3)) return;
        _lastClip = entry; _lastClipAt = DateTime.UtcNow;
        Add(new ToastWindow(this, "clip", entry, null, "Clip saved", ClipSubtitle(entry), Duration(_settings.ToastClip), false));
    });

    public void ShowShared(ClipEntry entry, string link) => Post(() =>
    {
        if (!_settings.ToastShared.Enabled) return;
        Add(new ToastWindow(this, "shared", entry, link, "Shared · link copied", link, Duration(_settings.ToastShared), false));
    });

    public void ShowRecordingStarted(string game) => Post(() =>
    {
        if (!_settings.ToastRecording.Enabled) return;
        Add(new ToastWindow(this, "recording", null, null, "Recording started", game, Duration(_settings.ToastRecording), false));
    });

    /// <summary>"Recording started" for a manual recording (kind "record", red dot; <see cref="ShowRecordingStarted"/> is the replay buffer). Follows the "Recording" switch.</summary>
    public void ShowRecordStarted(string text) => Post(() =>
    {
        if (!_settings.ToastRecording.Enabled) return;
        Add(new ToastWindow(this, "record", null, null, "Recording started", text, Duration(_settings.ToastRecording), false));
    });

    /// <summary>"Recording saved" with its length and size (kind "recorded", green dot). Follows the "Recording" switch.</summary>
    public void ShowRecordSaved(string text) => Post(() =>
    {
        if (!_settings.ToastRecording.Enabled) return;
        Add(new ToastWindow(this, "recorded", null, null, "Recording saved", text, Duration(_settings.ToastRecording), false));
    });

    /// <summary>
    /// A plain title + line toast in the "Recording started" layout ("Recording paused"); the caller decides whether it is wanted.
    /// The dot is grey when <paramref name="paused"/> (as the status dot), accent otherwise: red stays "recording" / error.
    /// </summary>
    public void ShowNotice(string title, string text, bool paused = false) =>
        Post(() => Add(new ToastWindow(this, paused ? "paused" : "info", null, null, title, text, Duration(_settings.ToastRecording), false)));

    /// <summary>"Uploading to Copium…" for the share-last hotkey / tray / --share-last, which have no progress UI of their own. Follows the "Shared" switch.</summary>
    public void ShowUploading(ClipEntry entry, string title = "Uploading to Copium…") => Post(() =>
    {
        if (!_settings.ToastShared.Enabled) return;
        Add(new ToastWindow(this, "info", entry, null, title, ClipSubtitle(entry), Duration(_settings.ToastShared), false));
    });

    /// <summary>
    /// "Screenshot saved" (kind "screenshot", green dot) with the size and what happened to the clipboard, and Open / Folder buttons.
    /// Follows the "Screenshot saved" switch (<see cref="Settings.ToastScreenshot"/>). The picture itself is not shown: the toast stays small.
    /// </summary>
    public void ShowScreenshot(ScreenshotEntry entry, ScreenshotToastState state) => Post(() =>
    {
        if (!_settings.ToastScreenshot.Enabled) return;
        Add(new ToastWindow(this, "screenshot", null, null, "Screenshot saved", ScreenshotService.Subtitle(entry, state), Duration(_settings.ToastScreenshot), false, entry, state.CanUpload));
    });

    /// <summary>
    /// An upload of a screenshot began: the "Screenshot saved" toast that is still up for it turns into "Uploading…" with the
    /// percentage on its button (one is made when it is gone); the handle then shows the result. Silent when the toast is switched off.
    /// </summary>
    public IScreenshotUploadToast ShowScreenshotUploading(ScreenshotEntry entry)
    {
        var handle = new UploadToast(this, entry);
        Post(handle.Attach);
        return handle;
    }

    private sealed class UploadToast : IScreenshotUploadToast
    {
        private readonly Toasts _owner;
        private readonly ScreenshotEntry _entry;
        private ToastWindow? _window;

        public UploadToast(Toasts owner, ScreenshotEntry entry) { _owner = owner; _entry = entry; }

        /// <summary>The service's cancel for this upload (the toast's Cancel button calls it on the UI thread).</summary>
        public Action? OnCancel { get; set; }

        public void Attach()
        {
            if (!_owner._settings.ToastScreenshot.Enabled) return;
            _window = _owner._open.LastOrDefault(w => !w.IsDismissed && w.Shot != null && string.Equals(w.Shot.Path, _entry.Path, StringComparison.OrdinalIgnoreCase));
            if (_window == null)
            {
                _window = new ToastWindow(_owner, "screenshot", null, null, "Screenshot saved", ScreenshotService.Subtitle(_entry, default), Duration(_owner._settings.ToastScreenshot), false, _entry);
                _owner.Add(_window);
            }
            _window.CancelHandler = () => OnCancel?.Invoke();
            _window.BeginUpload();
        }

        public void Report(double fraction) => _owner.Post(() => _window?.SetUploadProgress(fraction));
        public void Succeeded(string link, bool linkCopied) => _owner.Post(() => _window?.UploadSucceeded(link, linkCopied));
        public void Failed() => _owner.Post(() => _window?.UploadFailed());
        public void Declined() => _owner.Post(() => _window?.UploadDeclined());
    }

    /// <summary>
    /// Why something asked for from the hotkey / tray did not happen ("Clip not saved", "Clip not uploaded" and the reason): no
    /// buttons, the text wraps, at least 8 s. Shown whatever the switches say: the caller only asks when nothing else would show it.
    /// </summary>
    public void ShowProblem(string title, string text) => Post(() =>
    {
        var seconds = Math.Max(8, Duration(_settings.ToastShared).TotalSeconds);
        Add(new ToastWindow(this, "problem", null, null, title, text, TimeSpan.FromSeconds(seconds), false));
    });

    /// <summary>
    /// A warning the user can silence: "Don't show again" runs <paramref name="dontShowAgain"/> and closes the toast; "Open setting"
    /// (only when <paramref name="openSetting"/> is given) runs that and leaves the toast open. The text wraps, at least 10 s.
    /// Shown whatever the switches say; the ✕ just closes it.
    /// </summary>
    public void ShowBorderNotice(string title, string text, Action dontShowAgain, Action? openSetting) => Post(() =>
    {
        var seconds = Math.Max(10, Duration(_settings.ToastRecording).TotalSeconds);
        Add(new ToastWindow(this, "border", null, null, title, text, TimeSpan.FromSeconds(seconds), false) { DontShowAction = dontShowAgain, OpenSettingAction = openSetting });
    });

    /// <summary>Re-stacks the open toasts after one changed its height.</summary>
    internal void Relayout() => Post(Layout);

    /// <summary>Shows a sample toast of a kind ("clip" | "shared" | "recording" | "screenshot") with the current position and duration, ignoring the enable switch.</summary>
    public void Preview(string kind) => Post(() =>
    {
        string game = QuietMode.SampleToastGame(AppController.DemoGame);
        var sample = new ClipEntry { Path = game + " 2026-10-20 21-04-11.mp4", Game = game, Duration = 90, Size = 190_000_000, Created = DateTime.Now };
        switch ((kind ?? "clip").Trim().ToLowerInvariant())
        {
            case "shared":
                Add(new ToastWindow(this, "shared", sample, "https://copium.pics/v/example", "Shared · link copied", "https://copium.pics/v/example", Duration(_settings.ToastShared), true));
                break;
            case "recording":
                Add(new ToastWindow(this, "recording", null, null, "Recording started", game, Duration(_settings.ToastRecording), true));
                break;
            case "screenshot":
                var shot = new ScreenshotEntry { Path = "Screenshot 2026-10-20 21-04-11.png", Width = 2560, Height = 1440, Size = 1_200_000, Created = DateTime.Now };
                Add(new ToastWindow(this, "screenshot", null, null, "Screenshot saved", ScreenshotService.Subtitle(shot, new ScreenshotToastState(true, false)), Duration(_settings.ToastScreenshot), true, shot, canUpload: true));
                break;
            default:
                Add(new ToastWindow(this, "clip", sample, null, "Clip saved", ClipSubtitle(sample), Duration(_settings.ToastClip), true));
                break;
        }
    });

    /// <summary>Fades every open toast out (each closes when its fade ends); nothing is re-stacked for them.</summary>
    public void CloseAll() => Post(() => { foreach (var t in _open.ToList()) t.Dismiss(); _open.Clear(); });

    /// <summary>Fades out every open toast of one kind (e.g. "border"); the others stay.</summary>
    public void CloseKind(string kind) => Post(() => { foreach (var t in _open.Where(x => x.Kind == kind).ToList()) t.Dismiss(); });

    private void Post(Action a)
    {
        if (_ui.CheckAccess()) a(); else _ui.BeginInvoke(a);
    }

    private static TimeSpan Duration(ToastSetting s) => TimeSpan.FromSeconds(Math.Clamp(s.Seconds, 1, 30));

    private static string ClipSubtitle(ClipEntry e)
    {
        string game = string.IsNullOrEmpty(e.Game) ? "" : e.Game + " · ";
        // Decimal MB, the unit the Copium / Discord limits and the Share pre-check use, so a size never reads as under a limit it is over.
        return $"{game}{Capabilities.FormatClipLength(e.Duration)} · {Capabilities.FormatBytes(e.Size)}";
    }

    private void Add(ToastWindow t)
    {
        _open.Add(t);
        t.Closed += (_, _) => { if (_open.Remove(t)) Layout(); };   // the others move up only once the fade-out has finished
        t.Opacity = 0;             // shown invisible, placed, then faded in: no flash at the wrong spot
        t.Show();
        t.UpdateLayout();
        Layout();
        t.FadeIn();
        t.StartTimer();
    }

    internal void Dismiss(ToastWindow t)
    {
        try { t.Dismiss(); } catch { }
    }

    /// <summary>Positions every open toast: anchored at the chosen edge/corner of the captured monitor's work area, newest nearest the edge.</summary>
    private void Layout()
    {
        if (_open.Count == 0) return;
        var wa = ScreenPlacement.WorkAreaOf(_monitor());
        string pos = (_settings.ToastPosition ?? "bottom-right").Trim().ToLowerInvariant();
        bool top = pos.StartsWith("top"), bottom = pos.StartsWith("bottom");
        bool left = pos.EndsWith("left"), right = pos.EndsWith("right");
        var newestFirst = Enumerable.Reverse(_open).ToList();

        // Two passes: moving a window onto a monitor with another DPI rescales it (PerMonitorV2), so sizes are re-read once.
        for (int pass = 0; pass < 2; pass++)
        {
            var sizes = newestFirst.Select(t => ScreenPlacement.SizeOf(t)).ToList();
            double scale = ScreenPlacement.ScaleOf(newestFirst[0]);
            int margin = (int)Math.Round(Margin * scale), gap = (int)Math.Round(Gap * scale);
            int total = sizes.Sum(s => s.h) + gap * (sizes.Count - 1);
            int y = top ? wa.top + margin : bottom ? wa.bottom - margin : (wa.top + wa.bottom - total) / 2;
            bool changed = false;
            for (int i = 0; i < newestFirst.Count; i++)
            {
                var (w, h) = sizes[i];
                int x = left ? wa.left + margin : right ? wa.right - margin - w : (wa.left + wa.right - w) / 2;
                int yy = bottom ? y - h : y;
                changed |= ScreenPlacement.Move(newestFirst[i], x, yy);
                y = bottom ? yy - gap : yy + h + gap;
            }
            if (!changed) break;
        }
    }
}

/// <summary>
/// One toast. Created and shown by <see cref="Toasts"/>; fades in, hides itself after its duration (paused while the mouse is
/// over it), and every way out (the timer, the close button, a button action, <see cref="Toasts.CloseAll"/>) goes through
/// <see cref="Dismiss"/>: a fade-out, then the real Close().
/// </summary>
public partial class ToastWindow : Window, ICaptureExcludedWindow
{
    private static readonly TimeSpan FadeInTime = TimeSpan.FromMilliseconds(180), FadeOutTime = TimeSpan.FromMilliseconds(260);

    private readonly Toasts? _owner;
    private readonly DispatcherTimer _hide = new();
    private readonly TimeSpan _duration;
    private readonly bool _preview;
    private bool _busy;
    private bool _menuOpen;      // the Share menu is open: the toast stays up (no fade, no hide timer)
    private System.Windows.Controls.ContextMenu? _menu;
    private bool _dismissed;     // fading out; Close() follows when the fade ends

    internal string Kind { get; } = "clip";
    internal ClipEntry? Entry { get; }
    internal string? Link { get; }
    /// <summary>The screenshot a "screenshot" toast is about (Open / Folder act on its file).</summary>
    internal ScreenshotEntry? Shot { get; }
    internal bool IsDismissed => _dismissed;
    private string? _shotLink;                        // the Copium link of a screenshot once it is uploaded
    private bool _uploading;                          // an upload of the screenshot is running: its button is Cancel
    private string? _subtitleBeforeUpload;
    /// <summary>What the Cancel button of a running upload does (set by the upload handle).</summary>
    internal Action? CancelHandler { get; set; }
    internal Action? DontShowAction { get; init; }
    internal Action? OpenSettingAction { get => _openSetting; init { _openSetting = value; OpenSettingButton.Visibility = value != null ? Visibility.Visible : Visibility.Collapsed; } }
    private Action? _openSetting;

    internal ToastWindow(Toasts owner, string kind, ClipEntry? entry, string? link, string title, string subtitle, TimeSpan duration, bool preview, ScreenshotEntry? shot = null,
        bool canUpload = false)
    {
        InitializeComponent();
        if (AppController.ShowsDevMarker)
        {
            // a test (--dev) instance (unless --dev-no-marker): a yellow "TEST" tag by the title and the same yellow frame as its main window
            TestTag.Visibility = Visibility.Visible;
            Card.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(DevInstanceMarker.YellowHex));
            Card.BorderThickness = new Thickness(3);
            Card.Padding = new Thickness(12, 10, 12, 10);   // the frame is 2 px thicker per side: the inside stays as wide and as high as a normal toast's
        }
        _owner = owner;
        Kind = kind;
        Entry = entry;
        Link = link;
        Shot = shot;
        _duration = duration;
        _preview = preview;
        TitleText.Text = title;
        Subtitle.Text = subtitle;
        Subtitle.ToolTip = subtitle;
        Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, kind switch { "shared" or "info" => "Accent", "recording" or "record" => "Danger", "problem" or "border" => "Warn", "paused" => "Muted", _ => "Good" });
        if (kind is "problem" or "border") Subtitle.TextWrapping = TextWrapping.Wrap;   // the reason is the point: all of it, not an ellipsis
        NoticeButtons.Visibility = kind == "border" ? Visibility.Visible : Visibility.Collapsed;
        ClipButtons.Visibility = kind == "clip" ? Visibility.Visible : Visibility.Collapsed;
        SharedButtons.Visibility = kind == "shared" ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotButtons.Visibility = kind == "screenshot" && shot != null ? Visibility.Visible : Visibility.Collapsed;
        if (kind == "screenshot" && shot != null)
        {
            if (!string.IsNullOrEmpty(shot.CopiumLink)) { _shotLink = shot.CopiumLink; ShowLinkButton(); }
            else if (canUpload && CloudOn) ShotUploadButton.Visibility = Visibility.Visible;   // (cloud sharing off: no Upload button)
        }
        // Cloud sharing off (Settings → App): nothing on a toast uploads. Existing links stay copyable.
        if (kind == "clip" && !CloudOn) ShareButton.Visibility = Visibility.Collapsed;
        // "Share" alone without a token: the button then opens the share window (the file to drag), it does not upload.
        if (kind == "clip")
        {
            ShareButton.Content = ShareChoices.ToastLabel(HasToken, CloudOn, entry?.IsShared == true);
            if (ShareChoices.ButtonName(HasToken, CloudOn, entry?.IsShared == true) is { } name) System.Windows.Automation.AutomationProperties.SetName(ShareButton, name);
        }
        _hide.Interval = duration;
        _hide.Tick += (_, _) => { if (!_busy && !_menuOpen) Dismiss(); };
        MouseEnter += (_, _) => _hide.Stop();
        MouseLeave += (_, _) => { if (!_busy && !_dismissed && !_menuOpen) _hide.Start(); };
        SourceInitialized += (_, _) => ScreenPlacement.ExcludeFromCaptureUnlessDocShots(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// A tooltip's popup is a window of its own: every tooltip on a toast's elements (a plain string or a ToolTip, wherever it
    /// is set) is kept out of capture like the toast it belongs to, as are those of the share window (<see cref="CaptureExclusion"/>).
    /// </summary>
    static ToastWindow() => CaptureExclusion.Register();

    /// <summary>v1 MainWindow compatibility (<c>new ToastWindow().Show(entry)</c>); the v2 UI goes through <see cref="AppController.Toasts"/>.</summary>
    public ToastWindow() : this(null!, "clip", null, null, "Clip saved", "", TimeSpan.FromSeconds(7), false) { }

    /// <summary>v1 API: forwards to the toast service.</summary>
    internal void Show(ClipEntry entry) => AppController.Instance?.Toasts.ShowClipSaved(entry);

    internal void StartTimer()
    {
        if (_dismissed) return;
        _hide.Stop(); _hide.Interval = _duration; _hide.Start();
    }

    /// <summary>Fades the shown, already placed window in over 180 ms. The base Opacity is 1, so the fade ends on the normal state.</summary>
    internal void FadeIn()
    {
        if (_dismissed) return;
        Opacity = 1;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeInTime) { FillBehavior = FillBehavior.Stop });
    }

    /// <summary>
    /// The one way a toast goes away: fades out over 260 ms from wherever its opacity is (a toast dismissed mid fade-in just
    /// turns around), then closes, so <see cref="Toasts"/> re-stacks the remaining toasts on Closed as before. Calling it again
    /// does nothing; the hide timer, hover and buttons are inert from here on.
    /// </summary>
    internal void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        _hide.Stop();
        if (_menu is { IsOpen: true }) _menu.IsOpen = false;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, FadeOutTime) { FillBehavior = FillBehavior.HoldEnd };
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hide.Stop();
        base.OnClosed(e);
    }

    /// <summary>The cloud kill switch is on (or there is no controller, as in a preview made by a test).</summary>
    private static bool CloudOn => AppController.Instance?.Settings.CloudSharingEnabled != false;

    private static bool HasToken => AppController.Instance?.HasCopiumToken == true;

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (Entry == null || _busy || _preview) return;
        if (ShareChoices.For(HasToken, CloudOn, Entry.IsShared) is { } items)
        {
            _hide.Stop();   // the menu is open: the toast stays (it restarts when the menu closes without a choice)
            bool picked = false;
            _menuOpen = true;
            _menu = ShareMenu.Open(ShareButton, items, choice =>
            {
                picked = true;
                _ = RunChoiceAsync(choice);
            }, onClosed: () => { _menuOpen = false; if (!picked && !_busy && !_dismissed && !IsMouseOver) StartTimer(); });
            return;
        }
        await UploadAsync();
    }

    private async Task RunChoiceAsync(ShareChoice choice)
    {
        try
        {
            switch (choice)
            {
                case ShareChoice.ShareFile:
                    if (Entry == null) break;
                    if (AppController.Instance.ShareFile(Entry)) { Dismiss(); break; }   // the share window took over
                    SetShareStatus("Share failed");
                    ShareButton.ToolTip = AppController.ShareWindowFailedText;
                    ShowSubtitle(AppController.ShareWindowFailedText, wrap: true);
                    _hide.Interval = TimeSpan.FromSeconds(8);
                    _hide.Start();
                    break;
                case ShareChoice.SaveCopy: await SaveCopyAsync(); break;
                default: await UploadAsync(); break;   // Upload / Copy link: what the button did before
            }
        }
        catch (Exception ex) { Log.Error("toast share choice", ex); _busy = false; }
    }

    /// <summary>"Save a copy…" from the toast: the dialog is owned by the toast, which stays up while it is open and while it copies.</summary>
    private async Task SaveCopyAsync()
    {
        if (Entry == null || _busy || _preview) return;
        var app = AppController.Instance;
        _busy = true;
        _hide.Stop();
        ShareButton.ToolTip = null;
        SetShareStatus("Saving…");
        var progress = new Progress<double>(p => SetShareStatus($"Saving… {p:P0}"));
        SaveResult result;
        try { result = await app.SaveCopyAsync(Entry, this, progress); }
        finally { _busy = false; }
        if (_dismissed) return;
        switch (result.Outcome)
        {
            case SaveOutcome.Saved:
            {
                string dir = System.IO.Path.GetDirectoryName(result.SavedTo) ?? "";
                string folder = System.IO.Path.GetFileName(dir) is { Length: > 0 } leaf ? leaf : dir;
                ShowSubtitle("Saved to " + folder, wrap: false);
                RestoreShareLabel();
                StartTimer();
                break;
            }
            case SaveOutcome.Cancelled:
                RestoreShareLabel();
                StartTimer();
                break;
            default:
                SetShareStatus("Save failed");
                ShareButton.ToolTip = result.Error;
                if (result.Error is string why) ShowSubtitle(why, wrap: true);
                _hide.Interval = TimeSpan.FromSeconds(8);
                _hide.Start();
                break;
        }
    }

    /// <summary>A status in place of the Share label ("Saving… 40%", "Upload failed"): the label's accessible name ("Share options") no longer fits it, so the content speaks for itself.</summary>
    private void SetShareStatus(string text)
    {
        ShareButton.Content = text;
        ShareButton.ClearValue(System.Windows.Automation.AutomationProperties.NameProperty);
    }

    /// <summary>The Share label back after a status, with its accessible name when it has one.</summary>
    private void RestoreShareLabel()
    {
        ShareButton.Content = ShareChoices.ToastLabel(HasToken, CloudOn, Entry?.IsShared == true);
        if (ShareChoices.ButtonName(HasToken, CloudOn, Entry?.IsShared == true) is { } name)
            System.Windows.Automation.AutomationProperties.SetName(ShareButton, name);
    }

    private void ShowSubtitle(string text, bool wrap)
    {
        Subtitle.Text = text;
        Subtitle.ToolTip = text;
        if (wrap) Subtitle.TextWrapping = TextWrapping.Wrap;
        UpdateLayout();
        _owner?.Relayout();          // it may have grown a line: keep the stack inside the work area
    }

    /// <summary>Uploads the clip to Copium (or copies its link; with no token opens the share window): what the Share button always did.</summary>
    private async Task UploadAsync()
    {
        if (Entry == null || _busy || _preview) return;
        _busy = true;
        _hide.Stop();
        ShareButton.IsEnabled = false;
        ShareButton.ToolTip = null;
        SetShareStatus("Uploading…");
        var progress = new Progress<double>(p => SetShareStatus($"Uploading… {p:P0}"));
        bool ok = await AppController.Instance.ShareAsync(Entry, progress);
        _busy = false;
        if (ok) { Dismiss(); return; }   // the "Shared" toast takes over
        var app = AppController.Instance;
        if (app.LastShareOutcome == ShareOutcome.FileShareOpened) { Dismiss(); return; }   // no Copium token: the share window took over
        if (_dismissed) return;          // closed while it was uploading
        if (app.LastShareOutcome == ShareOutcome.Declined)
        {
            // "Not now" in the Share with Copium? pop-up, or cloud sharing off: nothing failed. The button is as it was (or gone).
            RestoreShareLabel();
            ShareButton.IsEnabled = true;
            if (!CloudOn) ShareButton.Visibility = Visibility.Collapsed;
            StartTimer();
            return;
        }
        // Short labels (the card is 330 wide); the full refusal or failure reason is the tooltip. The rare "could not open the share
        // window" (no token) is no upload failure: the subtitle below names it.
        SetShareStatus(app.ShareRefusal != null ? (app.Settings.CopiumPlus ? "Over Plus limit" : "Over free limit")
            : app.LastShareOutcome == ShareOutcome.WindowFailed ? "Share failed" : "Upload failed");
        ShareButton.ToolTip = app.ShareRefusal ?? app.ShareError;
        if (app.ShareRefusal == null && app.ShareError is string why)
        {
            // The reason belongs here, where the user is looking: the footer that also has it is usually in the tray.
            Subtitle.Text = why;
            Subtitle.ToolTip = why;
            Subtitle.TextWrapping = TextWrapping.Wrap;
            UpdateLayout();
            _owner?.Relayout();          // it may have grown a line: keep the stack inside the work area
        }
        ShareButton.IsEnabled = true;
        _hide.Interval = TimeSpan.FromSeconds(8);
        _hide.Start();
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (Entry == null || _preview) return;
        Dismiss();
        var app = AppController.Instance;
        if (!app.RequestTrim(Entry))
        {
            var main = System.Windows.Application.Current?.MainWindow;
            new TrimWindow(Entry) { Owner = main?.IsVisible == true ? main : null }.Show();
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (Entry != null && !_preview) AppController.Instance.OpenFolder(Entry.Path);
        Dismiss();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Link != null && !_preview) AppController.Instance.CopyToClipboard(Link);
        CopyButton.Content = "Copied ✓";
        StartTimer();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        string? url = Entry?.CopiumPageUrl ?? Link;
        if (url != null && !_preview)
        {
            // The address came from the server or from the clip list file: only an https address is handed to the shell.
            if (!CopiumClient.IsHttpsUrl(url, out var page)) Log.Warn("toast: not opened: the clip's link is not an https address");
            else
            {
                try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { AppController.Instance.Say("Could not open the browser: " + ex.Message); }
            }
        }
        Dismiss();
    }

    // ------------------------------------------------------------------ a screenshot toast that uploads

    private void ShowLinkButton()
    {
        ShotUploadButton.Visibility = Visibility.Visible;
        ShotUploadButton.IsEnabled = true;
        ShotUploadButton.Content = "Copy link";
        ShotUploadButton.ToolTip = "Copy the Copium link";
        ShotUploadButton.SetResourceReference(StyleProperty, "AccentButton");
    }

    /// <summary>The upload began (idempotent): the toast stays until it ends, and its button shows the progress.</summary>
    internal void BeginUpload()
    {
        if (_dismissed || Shot == null) return;
        _busy = true;
        _hide.Stop();
        if (!_uploading) _subtitleBeforeUpload = Subtitle.Text;
        _uploading = true;
        TitleText.Text = "Uploading screenshot…";
        Subtitle.Text = "Uploading…";
        // The button is the way out of a slow or stuck upload: Cancel (the progress is the second line, there is no room for both).
        ShotUploadButton.Visibility = Visibility.Visible;
        ShotUploadButton.IsEnabled = CancelHandler != null;   // an upload still queued behind another has nothing to cancel yet
        ShotUploadButton.Content = "Cancel";
        ShotUploadButton.ToolTip = "Stop uploading this screenshot (it stays saved; Retry uploads it again)";
        System.Windows.Automation.AutomationProperties.SetName(ShotUploadButton, "Cancel the upload");
    }

    internal void SetUploadProgress(double fraction)
    {
        if (_dismissed || !_busy) return;
        Subtitle.Text = $"Uploading… {Math.Clamp(fraction, 0, 1):P0}";
    }

    /// <summary>The upload is over (linked, failed, cancelled): the button is an ordinary one again and the second line is back.</summary>
    private void EndUploadState()
    {
        _uploading = false;
        CancelHandler = null;
        if (_subtitleBeforeUpload != null) { Subtitle.Text = _subtitleBeforeUpload; _subtitleBeforeUpload = null; }
        System.Windows.Automation.AutomationProperties.SetName(ShotUploadButton, "Upload the screenshot to Copium");
    }

    internal void UploadSucceeded(string link, bool linkCopied)
    {
        _busy = false;
        if (_dismissed || Shot == null) return;
        EndUploadState();
        _shotLink = link;
        TitleText.Text = linkCopied ? "Screenshot saved · link copied" : "Screenshot saved · uploaded";
        Subtitle.Text = link;
        Subtitle.ToolTip = link;
        ShowLinkButton();
        if (!string.IsNullOrEmpty(Shot.CopiumPageUrl)) ShotOpenButton.ToolTip = "Open the screenshot on Copium";
        StartTimer();
    }

    /// <summary>The upload ended without a link: back to the saved state, with Retry. The reason is a toast of its own.</summary>
    internal void UploadFailed()
    {
        _busy = false;
        if (_dismissed || Shot == null) return;
        EndUploadState();
        TitleText.Text = "Screenshot saved";
        ShotUploadButton.IsEnabled = true;
        ShotUploadButton.Content = "Retry";
        ShotUploadButton.ToolTip = "Upload the screenshot to Copium again";
        StartTimer();
    }

    /// <summary>The upload was stopped because cloud sharing was turned off: back to the saved state, neutral, no Upload / Retry button.</summary>
    internal void UploadDeclined()
    {
        _busy = false;
        if (_dismissed || Shot == null) return;
        EndUploadState();
        TitleText.Text = "Screenshot saved";
        ShotUploadButton.Visibility = Visibility.Collapsed;
        StartTimer();
    }

    private async void ShotUpload_Click(object sender, RoutedEventArgs e)
    {
        if (Shot == null || _preview) return;
        if (_uploading)
        {
            // Cancel: the service stops the HTTP call; the toast then goes back to Retry (UploadFailed).
            ShotUploadButton.IsEnabled = false;
            ShotUploadButton.Content = "Cancelling…";
            CancelHandler?.Invoke();
            return;
        }
        if (_busy) return;
        if (_shotLink is string link)
        {
            AppController.Instance.CopyToClipboard(link);   // an existing link stays copyable whatever the kill switch says
            ShotUploadButton.Content = "Copied ✓";
            StartTimer();
            return;
        }
        // Sharing is opt-in: the first Upload asks (the pop-up), and cloud sharing off refuses. The toast stays as it was when declined.
        _busy = true;
        _hide.Stop();
        bool allowed;
        try { allowed = await AppController.Instance.EnsureCopiumConsentAsync(ConsentContext.ScreenshotUploadManual); }
        finally { _busy = false; }
        if (!allowed)
        {
            if (!CloudOn && !_dismissed) ShotUploadButton.Visibility = Visibility.Collapsed;
            StartTimer();
            return;
        }
        if (_dismissed) return;
        BeginUpload();
        bool ok = false;
        try { ok = await AppController.Instance.Screenshots.UploadAsync(Shot, manual: true); }
        catch (Exception ex) { Log.Error("screenshot: upload", ex); }
        // The upload reports its own end through the toast service; this covers an upload that never started or was already done.
        if (!_busy || _dismissed) return;
        if (ok && Shot.CopiumLink is string done) UploadSucceeded(done, false);
        else UploadFailed();
    }

    private void ShotOpen_Click(object sender, RoutedEventArgs e)
    {
        // Uploaded: the page on Copium (an https address only: it came from the server); otherwise the file.
        if (Shot != null && !_preview && _shotLink != null && Uri.TryCreate(Shot.CopiumPageUrl, UriKind.Absolute, out var page) && page.Scheme == Uri.UriSchemeHttps)
        {
            try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { AppController.Instance.Say("Could not open the browser: " + ex.Message); }
            Dismiss();
            return;
        }
        if (Shot != null && !_preview && System.IO.File.Exists(Shot.Path))
        {
            // Shell-executed only as a .png inside the current screenshots folder or a folder screenshots were recorded as saved to
            // (never a folder that only a history entry names, nor a network path while the folder is local): a path from the history file must never start a program.
            if (!AppController.Instance.Screenshots.IsOpenableEntry(Shot.Path))
            {
                Log.Warn("screenshot: not opened: not a .png in a screenshots folder");
                AppController.Instance.Say("Could not open the screenshot: it is not in the screenshots folder");
            }
            else
            {
                try { Process.Start(new ProcessStartInfo(Shot.Path) { UseShellExecute = true }); }
                catch (Exception ex) { AppController.Instance.Say("Could not open the screenshot: " + ex.Message); }
            }
        }
        Dismiss();
    }

    private void ShotEdit_Click(object sender, RoutedEventArgs e)
    {
        // The service checks the file (a screenshot of this app, a readable .png) and opens the editor, or says why not.
        if (Shot != null && !_preview) AppController.Instance.Screenshots.EditFile(Shot.Path);
        Dismiss();
    }

    private void ShotFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Shot != null && !_preview)
        {
            // The file is gone (deleted, moved): the screenshots folder, never the clips folder OpenFolder falls back to.
            if (System.IO.File.Exists(Shot.Path)) AppController.Instance.OpenFolder(Shot.Path);
            else AppController.Instance.OpenScreenshotsFolder();
        }
        Dismiss();
    }

    private void DontShow_Click(object sender, RoutedEventArgs e)
    {
        try { DontShowAction?.Invoke(); }
        catch (Exception ex) { Log.Warn("Don't show again: " + ex.Message); }
        Dismiss();
    }

    private void OpenSetting_Click(object sender, RoutedEventArgs e)
    {
        try { OpenSettingAction?.Invoke(); }
        catch (Exception ex) { Log.Warn("Open setting: " + ex.Message); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();
}

/// <summary>
/// Physical-pixel placement of topmost helper windows (toasts, status dot) on a given monitor. WPF's Left/Top are DIPs of whatever
/// monitor the window was created on; with PerMonitorV2 the only reliable way to put a window on another monitor is SetWindowPos.
/// </summary>
internal static unsafe class ScreenPlacement
{
    /// <summary>The monitor's work area (physical pixels, taskbar excluded). Survives a stale handle after a display change.</summary>
    public static RECT WorkAreaOf(MonitorInfo? mon)
    {
        if (mon != null)
        {
            if (TryWorkArea(new HMONITOR(mon.Handle), out var wa)) return wa;
            var hm = PInvoke.MonitorFromPoint(new System.Drawing.Point(mon.Left + mon.Width / 2, mon.Top + mon.Height / 2), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            if (TryWorkArea(hm, out wa)) return wa;
            return new RECT(mon.Left, mon.Top, mon.Left + mon.Width, mon.Top + mon.Height);
        }
        var primary = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new RECT(primary);
    }

    private static bool TryWorkArea(HMONITOR hm, out RECT work)
    {
        work = default;
        if (hm.IsNull) return false;
        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);
        if (!PInvoke.GetMonitorInfo(hm, ref mi)) return false;
        work = mi.rcWork;
        return true;
    }

    public static HWND HwndOf(Window w) => new(new WindowInteropHelper(w).Handle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowDisplayAffinity(nint hwnd, uint affinity);
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>
    /// Keeps a window out of every screen capture, Windows Graphics Capture included (WDA_EXCLUDEFROMCAPTURE, Windows 10 2004+):
    /// toasts and the status dot sit on the captured monitor and would otherwise be recorded into the clips. Older builds refuse
    /// it and the window simply stays capturable; WDA_MONITOR is deliberately not a fallback, it would put a black box in the clip.
    /// </summary>
    public static void ExcludeFromCapture(nint hwnd)
    {
        if (hwnd != 0) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>
    /// <see cref="ExcludeFromCapture"/> for the toast and the share window only: in documentation-screenshot mode (<see cref="AppController.DocShots"/>)
    /// they are off-screen and the display affinity would blank their PrintWindow pictures. Every other window always stays excluded.
    /// </summary>
    public static void ExcludeFromCaptureUnlessDocShots(nint hwnd)
    {
        if (!AppController.DocShots) ExcludeFromCapture(hwnd);
    }

    /// <summary>Current size in physical pixels.</summary>
    public static (int w, int h) SizeOf(Window w)
    {
        var hwnd = HwndOf(w);
        if (!hwnd.IsNull && PInvoke.GetWindowRect(hwnd, out RECT rc)) return (rc.right - rc.left, rc.bottom - rc.top);
        return ((int)w.ActualWidth, (int)w.ActualHeight);
    }

    /// <summary>Physical pixels per DIP of the window (its current monitor).</summary>
    public static double ScaleOf(Window w)
    {
        var hwnd = HwndOf(w);
        if (hwnd.IsNull) return 1;
        uint dpi = PInvoke.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    /// <summary>Moves the window (topmost, without activating it). Returns true when its size changed as a result (DPI change).</summary>
    public static bool Move(Window w, int x, int y)
    {
        var hwnd = HwndOf(w);
        if (hwnd.IsNull) return false;
        if (QuietMode.Active) { QuietMode.Conceal((nint)hwnd.Value); return false; }   // --dev-quiet: toasts, the dot and the share window stay off-screen, behind everything
        var before = SizeOf(w);
        PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, x, y, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        return SizeOf(w) != before;
    }

    /// <summary>Places a window with a position computed from its physical size, re-measuring once if the move rescaled it.</summary>
    public static void Place(Window w, Func<int, int, (int x, int y)> position)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var (pw, ph) = SizeOf(w);
            var (x, y) = position(pw, ph);
            if (!Move(w, x, y)) break;
        }
    }
}
