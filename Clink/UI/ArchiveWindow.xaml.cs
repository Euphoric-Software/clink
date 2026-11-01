using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clink.Services;
using Clink.Services.Localization;

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
    private bool _refused;                          // the destination test refused the location: Archive stays off until a later test passes

    internal ArchiveWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        _destination = (app.Settings.ArchivePath ?? "").Trim();
        DestinationText.Text = _destination.Length > 0 ? L.Ltr(_destination) : L.T("No archive location is set");
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
            // "Storage" is the STORAGE section heading on the Clips tab of Settings (translate it the same way)
            ShowDestination(L.T("Set an archive location under Storage first"), ok: false);
            return;
        }
        (bool ok, string message, long freeBytes) answer;
        try { answer = await _app.Archive.TestAsync(_destination); }
        catch (Exception ex) { answer = (false, ex.Message, 0); }
        if (!_closed) ShowDestination(answer.message, answer.ok);
    }

    /// <summary>Shows the test's answer. A refusal (the clips folder under another name, unreachable, not writable) turns Archive off, so a press does not repeat it; a later answer that passes turns it on again.</summary>
    private void ShowDestination(string message, bool ok)
    {
        DestinationInfo.Text = message;
        DestinationInfo.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Good" : "Warn");
        _refused = !ok;
        if (_preview != null) UpdateControls();   // until the first count is in, that count's own UpdateControls takes this up
    }

    /// <summary>Counts what each choice would move (on a worker: it reads the clips folder) and puts the numbers in the labels.</summary>
    private async Task RefreshCountsAsync(bool first)
    {
        int gen = ++_countGen;
        DueTitle.Text = L.T("Everything due by your rules (counting…)");
        AllTitle.Text = L.T("All local clips (counting…)");
        ArchivePreview preview;
        try { preview = await Task.Run(_app.Archive.Preview); }
        catch (Exception ex) { Log.Warn("archive window: counting the clips failed: " + ex.Message); preview = default; }
        if (_closed || gen != _countGen) return;

        _preview = preview;
        DueTitle.Text = HasRule ? L.T("Everything due by your rules ({counts})", ("counts", CountText(preview.DueCount, preview.DueBytes))) : L.T("Everything due by your rules");
        AllTitle.Text = L.T("All local clips ({counts})", ("counts", CountText(preview.AllCount, preview.AllBytes)));
        // Nothing due (or no rule at all): the user came to move something, so "all" is the one to confirm. It is only a
        // preselection, on opening: after a run the choice stays where the user left it.
        if (first) (HasRule && preview.DueCount > 0 ? DueRadio : AllRadio).IsChecked = true;
        if (first && preview.AllCount == 0) StatusText.Text = L.T("There are no clips in the clips folder to move.");
        UpdateControls();
    }

    private void Scope_Changed(object sender, RoutedEventArgs e) => UpdateControls();

    private void UpdateControls()
    {
        DueRadio.IsEnabled = HasRule && !_running;
        AllRadio.IsEnabled = !_running;
        int count = _preview is { } p ? (AllRadio.IsChecked == true ? p.AllCount : DueRadio.IsChecked == true ? p.DueCount : 0) : 0;
        ArchiveButton.IsEnabled = !_running && count > 0 && _destination.Length > 0 && !_refused;
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
        StatusText.Text = L.T("Checking the archive location…");
        UpdateControls();

        ArchiveRunResult result;
        try
        {
            // While a game runs the copy keeps the gentle speed of the automatic moves; with no game it goes at full speed. The
            // service asks again for every chunk, so a game that starts (or ends) during a long run is followed.
            result = await _app.Archive.RunManualAsync(scope, () => _app.CurrentGame != null, new Progress<ArchiveProgress>(OnProgress), _cts.Token);
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
        StatusText.Text = L.T("Cancelling…");
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
    internal static string Clips(int n) => L.Plural("{count} clips", n);

    /// <summary>"12 clips · 3.4 GB" (the size as the archive shows sizes), "0 clips" for none.</summary>
    internal static string CountText(int count, long bytes) =>
        count <= 0 ? Clips(0) : L.T("{clips} · {size}", ("clips", Clips(count)), ("size", ArchiveService.FormatBytes(bytes)));

    /// <summary>The automatic rules in words: "older than 1 day / while the folder is over 100 GB", "no rule set" when both are off.</summary>
    internal static string RuleText(int days, double gb)
    {
        string? age = days > 0 ? L.Plural("older than {count} days", days) : null;
        string? size = gb > 0 ? L.T("while the folder is over {gb} GB", ("gb", gb.ToString("0.##", CultureInfo.InvariantCulture))) : null;   // (the format of the box where it is typed)
        return age != null && size != null ? L.T("{age} / {size}", ("age", age), ("size", size))
            : age ?? size ?? L.T("no rule set");
    }

    /// <summary>"Moving 3 of 12 · Copying clip.mp4 (40 %)".</summary>
    internal static string ProgressText(ArchiveProgress p) =>
        L.T("Moving {index} of {count} · Copying {file} ({percent} %)", ("index", p.Index), ("count", p.Count), ("file", L.Ltr(p.File)), ("percent", p.Percent));

    /// <summary>
    /// The line after a run: "Moved 12 clips (3.4 GB) to T:\clips · 1 in use, try again later", or what went wrong. The parts that
    /// are zero are left out; a run refused before it began is just its reason.
    /// </summary>
    internal static string ResultText(ArchiveRunResult r, string destination)
    {
        var parts = new List<string>();
        if (r.Moved > 0)
            parts.Add(L.Plural(r.Cancelled ? "Cancelled after moving {count} clips ({size}) to {destination}" : "Moved {count} clips ({size}) to {destination}", r.Moved,
                ("size", ArchiveService.FormatBytes(r.BytesMoved)), ("destination", L.Ltr(destination))));
        else if (r.Cancelled) parts.Add(L.T("Cancelled, nothing was moved"));
        else if (r.Eligible == 0 && !string.IsNullOrEmpty(r.Problem)) parts.Add(char.ToUpper(r.Problem[0], L.UiCulture) + r.Problem[1..]);   // refused: the reason is all there is
        else if (r.Eligible == 0) parts.Add(L.T("Nothing to move"));
        else parts.Add(L.T("Nothing was moved"));
        if (r.InUse > 0) parts.Add(L.Plural("{count} in use, try again later", r.InUse));
        if (r.TooNew > 0) parts.Add(L.Plural("{count} saved less than {age} ago, try again later", r.TooNew, ("age", L.Duration(ArchiveService.MinAgeSeconds))));
        if (r.Failed > 0) parts.Add(r.Problem != null ? L.Plural("{count} failed: {problem}", r.Failed, ("problem", r.Problem)) : L.Plural("{count} failed", r.Failed));
        return string.Join(" · ", parts);   // (the separator of the parts: S6 looks at it with the other sentence joins)
    }
}
