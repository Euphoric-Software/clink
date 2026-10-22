using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// "Archive now…" (docs/SPEC-v2.md §9): a modal dialog over the main window that moves clips to the archive location by hand.
/// It shows where they go (the Test button's answer) and what there is to move, counted off the UI thread: everything the
/// automatic rules would pick now, and all local clips, which is preselected when nothing is due. Archive runs
/// <see cref="ArchiveService.RunManualAsync"/> (full speed, or the gentle automatic speed while a game runs), with a progress
/// bar and "Moving 3 of 12 · Copying clip.mp4 (40 %)", Cancel, and at the end a line saying what was moved and what was left
/// or went wrong. Closing the window cancels a run in progress; the service deletes the partial copy.
/// Needs only an archive location, not the automatic switch and not a rule. Every colour is a DynamicResource.
/// </summary>
public partial class ArchiveWindow : Window
{
    private readonly AppController _app;
    private readonly string _destination;           // the archive location this window was opened for
    private CancellationTokenSource? _cts;
    private ArchivePreview? _preview;
    private bool _running, _cancelling, _closed;
    private int _countGen;                          // a count that was asked for again replaces the one still on its way

    internal ArchiveWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        _destination = (app.Settings.ArchivePath ?? "").Trim();
        DestinationText.Text = _destination.Length > 0 ? _destination : "No archive location is set";
        DestinationText.ToolTip = _destination.Length > 0 ? _destination : null;
        DueRule.Text = RuleText(app.Settings.ArchiveAfterDays, app.Settings.ArchiveWhenLocalExceedsGB);
        Loaded += async (_, _) => { _ = ShowDestinationAsync(); await RefreshCountsAsync(first: true); };
        Closing += (_, _) => { if (_running) _cts?.Cancel(); };
        Closed += (_, _) => _closed = true;
    }

    private bool HasRule => _app.Settings.ArchiveAfterDays > 0 || _app.Settings.ArchiveWhenLocalExceedsGB > 0;

    // ------------------------------------------------------------------ what there is to move

    /// <summary>The destination line: reachable, writable, free space (or what is wrong). The answer can take a while from an offline share, so it comes whenever it comes.</summary>
    private async Task ShowDestinationAsync()
    {
        if (_destination.Length == 0)
        {
            ShowDestination("Set an archive location under Storage first", ok: false);
            return;
        }
        (bool ok, string message, long freeBytes) answer;
        try { answer = await _app.Archive.TestAsync(_destination); }
        catch (Exception ex) { answer = (false, ex.Message, 0); }
        if (!_closed) ShowDestination(answer.message, answer.ok);
    }

    private void ShowDestination(string message, bool ok)
    {
        DestinationInfo.Text = message;
        DestinationInfo.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Good" : "Warn");
    }

    /// <summary>Counts what each choice would move (on a worker: it reads the clips folder) and puts the numbers in the labels.</summary>
    private async Task RefreshCountsAsync(bool first)
    {
        int gen = ++_countGen;
        DueTitle.Text = "Everything due by your rules (counting…)";
        AllTitle.Text = "All local clips (counting…)";
        ArchivePreview preview;
        try { preview = await Task.Run(_app.Archive.Preview); }
        catch (Exception ex) { Log.Warn("archive window: counting the clips failed: " + ex.Message); preview = default; }
        if (_closed || gen != _countGen) return;

        _preview = preview;
        DueTitle.Text = HasRule ? $"Everything due by your rules ({CountText(preview.DueCount, preview.DueBytes)})" : "Everything due by your rules";
        AllTitle.Text = $"All local clips ({CountText(preview.AllCount, preview.AllBytes)})";
        // Nothing due (or no rule at all): the user came to move something, so "all" is the one to confirm. It is only a
        // preselection, on opening: after a run the choice stays where the user left it.
        if (first) (HasRule && preview.DueCount > 0 ? DueRadio : AllRadio).IsChecked = true;
        if (first && preview.AllCount == 0) StatusText.Text = "There are no clips in the clips folder to move.";
        UpdateControls();
    }

    private void Scope_Changed(object sender, RoutedEventArgs e) => UpdateControls();

    private void UpdateControls()
    {
        DueRadio.IsEnabled = HasRule && !_running;
        AllRadio.IsEnabled = !_running;
        int count = _preview is { } p ? (AllRadio.IsChecked == true ? p.AllCount : DueRadio.IsChecked == true ? p.DueCount : 0) : 0;
        ArchiveButton.IsEnabled = !_running && count > 0 && _destination.Length > 0;
        ArchiveButton.Visibility = _running ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _running && !_cancelling;
        Bar.Visibility = _running ? Visibility.Visible : Visibility.Hidden;
    }

    // ------------------------------------------------------------------ a run

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _preview == null || _destination.Length == 0) return;
        var scope = AllRadio.IsChecked == true ? ArchiveScope.All : ArchiveScope.Due;
        _cts = new CancellationTokenSource();
        _running = true; _cancelling = false;
        Bar.Value = 0;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        StatusText.Text = "Checking the archive location…";
        UpdateControls();

        ArchiveRunResult result;
        try
        {
            // While a game runs the copy keeps the gentle speed of the automatic moves; with no game it goes at full speed.
            bool throttle = _app.CurrentGame != null;
            result = await _app.Archive.RunManualAsync(scope, throttle, new Progress<ArchiveProgress>(OnProgress), _cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("archive window: " + ex.Message);
            result = new ArchiveRunResult(0, 0, 0, 0, 0, 0, ex.Message, false);
        }
        if (_closed) return;   // closing cancelled it; nobody is left to tell

        _running = false;
        ShowResult(result);
        UpdateControls();
        _ = ShowDestinationAsync();               // the free space has changed
        await RefreshCountsAsync(first: false);   // what is left, for another go ("1 in use, try again later")
    }

    private void OnProgress(ArchiveProgress p)
    {
        if (!_running || _cancelling || _closed) return;
        Bar.Value = Math.Clamp(((p.Index - 1) + p.Percent / 100.0) / Math.Max(1, p.Count) * 100.0, 0, 100);
        StatusText.Text = ProgressText(p);
    }

    private void ShowResult(ArchiveRunResult r)
    {
        bool clean = r.Moved > 0 && !r.Cancelled && r.Failed == 0 && r.InUse == 0 && r.TooNew == 0 && r.Problem == null;
        StatusText.Text = ResultText(r, _destination);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, clean ? "Good" : r.Problem != null || r.Failed > 0 ? "Warn" : "Fg");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || _cancelling) return;
        _cancelling = true;
        _cts?.Cancel();
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        StatusText.Text = "Cancelling…";
        UpdateControls();
    }

    // ------------------------------------------------------------------ window

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    // ------------------------------------------------------------------ words (pure, tested)

    /// <summary>"1 clip", "12 clips".</summary>
    internal static string Clips(int n) => n == 1 ? "1 clip" : $"{n} clips";

    /// <summary>"12 clips · 3.4 GB" (the size as the archive shows sizes), "0 clips" for none.</summary>
    internal static string CountText(int count, long bytes) => count <= 0 ? "0 clips" : $"{Clips(count)} · {ArchiveService.FormatBytes(bytes)}";

    /// <summary>The automatic rules in words: "older than 1 day / while the folder is over 100 GB", "no rule set" when both are off.</summary>
    internal static string RuleText(int days, double gb)
    {
        var parts = new List<string>();
        if (days > 0) parts.Add(days == 1 ? "older than 1 day" : $"older than {days} days");
        if (gb > 0) parts.Add($"while the folder is over {gb.ToString("0.##", CultureInfo.InvariantCulture)} GB");
        return parts.Count == 0 ? "no rule set" : string.Join(" / ", parts);
    }

    /// <summary>"Moving 3 of 12 · Copying clip.mp4 (40 %)".</summary>
    internal static string ProgressText(ArchiveProgress p) => $"Moving {p.Index} of {p.Count} · Copying {p.File} ({p.Percent} %)";

    /// <summary>
    /// The line after a run: "Moved 12 clips (3.4 GB) to T:\clips · 1 in use, try again later", or what went wrong. The parts that
    /// are zero are left out; a run refused before it began is just its reason.
    /// </summary>
    internal static string ResultText(ArchiveRunResult r, string destination)
    {
        var parts = new List<string>();
        if (r.Moved > 0)
            parts.Add($"{(r.Cancelled ? "Cancelled after moving" : "Moved")} {Clips(r.Moved)} ({ArchiveService.FormatBytes(r.BytesMoved)}) to {destination}");
        else if (r.Cancelled) parts.Add("Cancelled, nothing was moved");
        else if (r.Eligible == 0 && !string.IsNullOrEmpty(r.Problem)) parts.Add(char.ToUpperInvariant(r.Problem[0]) + r.Problem[1..]);   // refused: the reason is all there is
        else if (r.Eligible == 0) parts.Add("Nothing to move");
        else parts.Add("Nothing was moved");
        if (r.InUse > 0) parts.Add($"{r.InUse} in use, try again later");
        if (r.TooNew > 0) parts.Add($"{r.TooNew} saved less than 30 s ago, try again later");
        if (r.Failed > 0) parts.Add(r.Problem != null ? $"{r.Failed} failed: {r.Problem}" : $"{r.Failed} failed");
        return string.Join(" · ", parts);
    }
}
