using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Clink.Native;
using Clink.Screenshots.Geometry;
using Clink.Screenshots.Imaging;
using Clink.Screenshots.Sources;
using Clink.Services;
using Clink.UI;

namespace Clink.Screenshots;

internal enum ScreenshotTrigger { Hotkey, Tray, Cli }

/// <summary>"none" | "before" | "after": what happens after the region is picked. Only "none" (save at once) exists in this version.</summary>
internal static class ScreenshotModes
{
    public const string None = "none", Before = "before", After = "after";
    public static readonly string[] All = { None, Before, After };

    /// <summary>The stored mode, lower-cased; anything unknown or blank is <see cref="None"/>.</summary>
    public static string Normalize(string? mode)
    {
        string m = (mode ?? "").Trim().ToLowerInvariant();
        return All.Contains(m) ? m : None;
    }
}

/// <summary>What the "Screenshot saved" toast says about the clipboard.</summary>
internal readonly record struct ScreenshotToastState(bool Copied, bool ClipboardFailed);

/// <summary>A committed region: the rectangle (virtual-screen pixels), the monitor that holds most of it (<see cref="SelectionMath.MonitorIndexOf"/>, an index into <see cref="FrozenDesktop.Monitors"/>) and how it was made.</summary>
internal sealed record RegionPickResult(RectI Rect, int MonitorIndex, PickOutcome HowEnded);

/// <summary>Shows the region picker over a frozen desktop and returns the region chosen, or null when the capture was cancelled.</summary>
internal interface IRegionPicker
{
    /// <summary>
    /// UI thread. Shows the overlay and completes when the selection is COMMITTED, with the overlay still on screen: the caller owns
    /// closing it (<see cref="Close"/>), so a later version can go on editing inside it. Completes with null when the capture was
    /// cancelled (Esc, right-click, <see cref="Cancel"/>, <paramref name="ct"/>, a change of the monitor layout, no input for a while,
    /// a window closed from outside); the overlay has then closed itself. At once with null when <paramref name="ct"/> is already cancelled.
    /// </summary>
    Task<RegionPickResult?> PickAsync(FrozenDesktop desktop, bool loupe, CancellationToken ct);
    /// <summary>Cancels the pick that is on screen (it completes with null and closes); nothing when there is none.</summary>
    void Cancel();
    /// <summary>Closes the overlay of the last pick (after a commit; harmless when it is closed already).</summary>
    void Close();
}

/// <summary>The toasts the screenshot subsystem shows (<see cref="Toasts"/>; a test records them instead).</summary>
internal interface IScreenshotToasts
{
    void ShowNotice(string title, string text, bool paused = false);
    void ShowProblem(string title, string text);
    void ShowScreenshot(ScreenshotEntry entry, ScreenshotToastState state);
}

/// <summary>What a saved screenshot is: the entry (in the history) and the encoded PNG (also for the clipboard).</summary>
internal sealed record ScreenshotSaveResult(ScreenshotEntry Entry, byte[] Png);

/// <summary>
/// The screenshot subsystem's coordinator (UI thread): hotkey / tray / CLI -> frozen desktop on a worker -> region picker -> crop ->
/// PNG on a worker -> history, clipboard, toast. Separate from the clip pipeline: own folder, own history, never a ClipEntry.
/// What can be tested without a screen is static (<see cref="SaveToDisk"/>, <see cref="FolderFor"/>, <see cref="Subtitle"/>).
/// </summary>
internal sealed class ScreenshotService
{
    internal static readonly TimeSpan TrayDelay = TimeSpan.FromMilliseconds(250);   // the tray menu fades out and is not capture-excluded
    private static readonly object s_nameGate = new();   // SaveToDisk: choosing a free name and taking it

    private readonly Settings _settings;
    private readonly IScreenshotToasts _toasts;
    private readonly IRegionPicker _picker;
    private readonly IReadOnlyList<IScreenshotSource> _sources;
    private readonly Func<IReadOnlyList<MonitorInfo>> _monitors;
    private readonly Func<string?> _game;
    private readonly Func<MonitorInfo?> _recordedMonitor;
    private ScreenshotHistory? _history;
    private string? _historyFolder;
    private bool _busy;
    private CancellationTokenSource? _pick;   // the capture under way; a second trigger cancels it

    /// <param name="monitors">The monitors to copy (default: <see cref="Native.Monitors.Enumerate"/>); a test passes made-up ones with <paramref name="sources"/>.</param>
    public ScreenshotService(Settings settings, IScreenshotToasts toasts, IRegionPicker picker, Func<string?> game, Func<MonitorInfo?> recordedMonitor,
        IReadOnlyList<IScreenshotSource>? sources = null, Func<IReadOnlyList<MonitorInfo>>? monitors = null)
    {
        _settings = settings;
        _toasts = toasts;
        _picker = picker;
        _game = game;
        _recordedMonitor = recordedMonitor;
        _sources = sources ?? new IScreenshotSource[] { new GdiScreenshotSource() };
        _monitors = monitors ?? (() => Native.Monitors.Enumerate());
    }

    /// <summary>A capture is in progress (frozen desktop being taken or the overlay up).</summary>
    public bool IsBusy => _busy;

    /// <summary>Where screenshots go: <see cref="Settings.ScreenshotsFolder"/>, else a <c>Screenshots</c> folder inside the clips folder (the library and the archive look at the clips folder's own files only).</summary>
    public static string FolderFor(Settings s) =>
        !string.IsNullOrWhiteSpace(s.ScreenshotsFolder) ? s.ScreenshotsFolder.Trim() : System.IO.Path.Combine(s.ClipsFolder, "Screenshots");

    private ScreenshotHistory History
    {
        get
        {
            string folder = FolderFor(_settings);
            if (_history == null || !string.Equals(_historyFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                _history = new ScreenshotHistory(ScreenshotHistory.DefaultFile, folder);
                _historyFolder = folder;
            }
            return _history;
        }
    }

    // ------------------------------------------------------------------ capture

    /// <summary>
    /// The screenshot hotkey / tray item / <c>--screenshot</c>. UI thread. Fire and forget: every failure is reported to the user
    /// here and nothing is thrown.
    /// </summary>
    public async void CaptureRegion(ScreenshotTrigger trigger)
    {
        try { await CaptureRegionAsync(trigger); }
        catch (Exception ex)
        {
            Log.Error("screenshot", ex);
            _toasts.ShowProblem("Screenshot failed", ex.Message);
        }
    }

    internal async Task CaptureRegionAsync(ScreenshotTrigger trigger)
    {
        if (!_settings.ScreenshotsEnabled)
        {
            Log.Info($"screenshot: ignored ({trigger}): screenshot tools are disabled");
            _toasts.ShowNotice("Screenshots are off", DisabledText);
            return;
        }
        if (_busy)
        {
            // A second press while a capture is under way CANCELS it (as ShareX does): the one way out that always works, also when
            // the overlay cannot take the keyboard (over a game that keeps the foreground) and Esc never reaches it.
            Log.Info($"screenshot: {trigger} while a capture is under way: cancelled");
            _pick?.Cancel();
            _picker.Cancel();
            return;
        }
        _busy = true;
        var cts = new CancellationTokenSource();
        _pick = cts;
        RegionPickResult? picked = null;
        FrozenDesktop? desktop = null;
        bool shown = false;
        var total = Stopwatch.StartNew();
        try
        {
            if (trigger == ScreenshotTrigger.Tray) await Task.Delay(TrayDelay);
            if (!cts.IsCancellationRequested)
            {
                // Before any window of ours exists: the copy of the screen is taken on a worker (GDI is thread-agnostic).
                var monitors = _monitors();
                desktop = await Task.Run(() => FrozenDesktop.CaptureAll(monitors, _sources));
                long captureMs = total.ElapsedMilliseconds;
                Log.Info($"screenshot: {trigger}, {desktop.Monitors.Count} monitor(s), virtual {desktop.VirtualBounds}, capture {captureMs} ms");
                if (!cts.IsCancellationRequested)
                {
                    shown = true;
                    picked = await _picker.PickAsync(desktop, _settings.ScreenshotLoupe, cts.Token);
                }
            }
        }
        catch (ScreenshotCaptureException ex)
        {
            Log.Warn("screenshot: " + ex.Message);
            _toasts.ShowProblem("Screenshot not taken", ex.Message);
            return;
        }
        finally
        {
            // The picker hands the overlay over still open after a commit (see IRegionPicker.PickAsync): nothing is edited in it yet.
            if (shown)
            {
                try { _picker.Close(); }
                catch (Exception ex) { Log.Warn("screenshot: overlay close: " + ex.Message); }
            }
            _pick = null;
            cts.Dispose();
            _busy = false;
        }

        if (desktop == null || picked is not { } pick || pick.Rect.IsEmpty)
        {
            Log.Info("screenshot: cancelled");
            return;
        }
        await CommitAsync(desktop, pick.Rect);
    }

    /// <summary>
    /// The overlay is closed: cuts the picture out of the frozen desktop, checks it for black and encodes and saves it on a worker
    /// (a 3 x 4K composite is ~100 MB: never on the UI thread), then (UI thread) clipboard, sound and toast.
    /// </summary>
    internal async Task CommitAsync(FrozenDesktop desktop, RectI rect)
    {
        if (_settings.ClickSound) ClickSound.Play(_settings.ClickVolume);
        var t = Stopwatch.StartNew();
        BitmapSource bitmap;
        ScreenshotSaveResult saved;
        bool black;
        try
        {
            string? game = GameFor(rect);
            string folder = FolderFor(_settings);
            var history = History;
            (bitmap, saved, black) = await Task.Run(() =>
            {
                var picture = desktop.Compose(rect);
                bool looksBlack = WarnBlack(desktop, rect, game != null);
                var made = picture.ToBitmapSource();   // once: the PNG is encoded from it and the clipboard (UI thread) gets the same frozen instance
                var result = SaveToDisk(picture, folder, game, DateTime.Now, history, made);
                return (made, result, looksBlack);
            });
        }
        catch (Exception ex)
        {
            Log.Error("screenshot: save", ex);
            _toasts.ShowProblem("Screenshot not saved", ex.Message);
            return;
        }
        long encodeMs = t.ElapsedMilliseconds;
        bool copied = false, failed = false;
        if (_settings.ScreenshotCopyImage)
        {
            copied = ScreenshotClipboard.Copy(bitmap, saved.Png);
            failed = !copied;
        }
        Log.Info($"screenshot: saved {saved.Entry.Path} ({saved.Entry.Width}x{saved.Entry.Height}, {saved.Entry.Size / 1024} KB), crop+encode+write {encodeMs} ms, clipboard {(copied ? "ok" : _settings.ScreenshotCopyImage ? "failed" : "off")}");
        if (black)
            _toasts.ShowProblem("Screenshot is black", "Saved, but the picture is all black. Is the game in exclusive fullscreen? Use borderless windowed mode.");
        else
            _toasts.ShowScreenshot(saved.Entry, new ScreenshotToastState(copied, failed));
    }

    internal const string DisabledText = "Screenshot tools are disabled in Settings";

    /// <summary>The detected game, when the picture overlaps the monitor that is being recorded; otherwise null (the name is then "Screenshot"). See <see cref="SelectionMath.GameFor"/>.</summary>
    private string? GameFor(RectI rect)
    {
        var mon = _recordedMonitor();
        return SelectionMath.GameFor(_game(), mon == null ? null : new RectI(mon.Left, mon.Top, mon.Width, mon.Height), rect);
    }

    // ------------------------------------------------------------------ pure-ish pieces

    /// <summary>
    /// The "Screenshot is black" warning (exclusive fullscreen) replaces the normal toast only when the picture looks black AND either
    /// every monitor under it is black as a whole or the detected game is on it. A dark crop of a lit desktop (a black terminal, a
    /// letterbox bar) gets the normal toast with its Open / Folder buttons.
    /// </summary>
    internal static bool WarnBlack(FrozenDesktop desktop, RectI rect, bool gameOnIt) =>
        desktop.LooksBlack(rect) && (gameOnIt || desktop.WholeMonitorsLookBlack(rect));

    /// <summary>
    /// The toast's Open button may hand <paramref name="path"/> to the shell only when it is a .png inside <paramref name="folder"/>
    /// (the screenshots folder): history entries are read from a file and flow through here too, and a shell-executed path is a
    /// program start.
    /// </summary>
    internal static bool IsOpenable(string? path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path) || !System.IO.Path.IsPathFullyQualified(folder)) return false;
            string full = System.IO.Path.GetFullPath(path);
            if (!string.Equals(System.IO.Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase)) return false;
            string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Encodes the image, writes it as <c>folder\{name}.png</c> (via a .partial file) and records it in the history. Picks the next
    /// free <c>" (n)"</c> name, again if another screenshot took it in the meantime. Worker thread or UI thread; throws on a disk problem.
    /// </summary>
    internal static ScreenshotSaveResult SaveToDisk(BgraImage image, string folder, string? game, DateTime when, ScreenshotHistory history, BitmapSource? bitmap = null)
    {
        if (image.Width <= 0 || image.Height <= 0) throw new InvalidOperationException("The picture is empty");
        Directory.CreateDirectory(folder);
        byte[] png = PngFile.Encode(bitmap ?? image.ToBitmapSource());
        string baseName = ScreenshotNaming.BaseName(game, when);
        string path = "";
        // The bytes go to a file of their own first, outside the lock (a big PNG must not hold the other saves up).
        string partial = System.IO.Path.Combine(folder, baseName + "." + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using (var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                fs.Write(png, 0, png.Length);
                fs.Flush(true);
            }
            // Picking the name and moving the file there is one step for this process (two saves in the same second take " (1)", " (2)"
            // in turn); another process (a --dev instance on the same folder) can still take a name in between: then the next one.
            lock (s_nameGate)
            {
                for (int attempt = 0; ; attempt++)
                {
                    path = ScreenshotNaming.UniquePath(folder, baseName);
                    try { File.Move(partial, path, overwrite: false); break; }
                    catch (IOException) when (attempt < 5 && File.Exists(path)) { }   // the name was taken between the check and the move
                }
            }
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
        var entry = new ScreenshotEntry
        {
            Path = path, Created = when, Width = image.Width, Height = image.Height, Size = png.Length,
            Game = string.IsNullOrWhiteSpace(game) ? null : game,
        };
        try { history.Add(entry); }
        catch (Exception ex) { Log.Warn("screenshot: history not updated: " + ex.Message); }   // the PNG is on disk: that is the result
        return new ScreenshotSaveResult(entry, png);
    }

    /// <summary>The toast's second line: "1280 x 720 · copied to clipboard" / "· clipboard busy, not copied" / just the size when copying is off.</summary>
    internal static string Subtitle(ScreenshotEntry e, ScreenshotToastState s)
    {
        string size = $"{e.Width} x {e.Height}";
        if (s.Copied) return size + " · copied to clipboard";
        if (s.ClipboardFailed) return size + " · clipboard busy, not copied";
        return size;
    }
}
