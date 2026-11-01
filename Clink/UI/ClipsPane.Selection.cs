using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clink.Screenshots;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The library's multi-select (the selection bar over the list): the models (<see cref="LibrarySelection{T}"/>, one per list kind), the
/// gestures (checkbox, Ctrl / Shift click, a plain click while something is selected, Ctrl+A, Esc), the bar and what its buttons do
/// (Share files…, Copy links, Save copies…, Delete). Every operation works on a snapshot of the selection taken when the button was
/// pressed; rows that leave the list meanwhile are skipped.
/// </summary>
public partial class ClipsPane
{
    private readonly LibrarySelection<ClipRow> _clipSel = new();
    private readonly LibrarySelection<ScreenshotRow> _shotSel = new();

    private enum BarBusy { None, Working, Saving, Uploading }

    private BarBusy _busy;
    private string? _barSignature;
    private readonly List<(Button Button, bool Enabled)> _barButtons = new();   // the bar's current buttons and whether each is on when nothing is working
    private CancellationTokenSource? _saveCts;
    private CancellationTokenSource? _uploadCts;
    private bool _uploadStopping;   // Cancel was pressed: the bulk upload stops after the item in hand

    /// <summary>Saving copies or uploading: the bar holds the only Cancel, so it stays, Clear is off and Esc does nothing.</summary>
    private bool LongRunning => _busy is BarBusy.Saving or BarBusy.Uploading;

    private IReadOnlyList<ClipRow> VisibleClips => _level == LibraryLevel.Shared ? _shared : _local;
    private bool OnShots => Feature == LibraryFeature.Screenshots;

    // ------------------------------------------------------------------ the models and the rows

    private void ClearSelections()
    {
        _clipSel.Clear();
        _shotSel.Clear();
        ApplySelection();
    }

    /// <summary>Selection is a subset of what is listed: drops what left the list (a collapsed clip's trims, a deleted file, another filter).</summary>
    private void PruneSelection()
    {
        _clipSel.Prune(VisibleClips);
        _shotSel.Prune(_shots);
        ApplySelection();
    }

    /// <summary>Puts the models on the rows (highlight, checkbox, "selection mode") and refreshes the bar.</summary>
    private void ApplySelection()
    {
        if (App == null) return;
        bool shots = OnShots;
        bool clipMode = !shots && _clipSel.Active, shotMode = shots && _shotSel.Active;
        foreach (var r in _rows.Values) r.SetSelection(_clipSel.Contains(r), clipMode);
        foreach (var r in _shotRows.Values) r.SetSelection(_shotSel.Contains(r), shotMode);
        UpdateBar();
    }

    private bool SelectionActive => OnShots ? _shotSel.Active : _clipSel.Active;

    /// <summary>What a click on a row's body does to the selection: Ctrl = toggle, Shift = range, both = add the range, a plain click toggles while something is selected; null = not a selecting click.</summary>
    private static SelectGesture? GestureFor(bool selecting)
    {
        var m = Keyboard.Modifiers;
        bool ctrl = (m & ModifierKeys.Control) != 0, shift = (m & ModifierKeys.Shift) != 0;
        if (ctrl && shift) return SelectGesture.AddRange;
        if (shift) return SelectGesture.Range;
        if (ctrl || selecting) return SelectGesture.Toggle;
        return null;
    }

    /// <summary>
    /// The row's checkbox changed (mouse, Space, or UI Automation's Toggle, which raises no Click): driven by the box's state, compared
    /// with the model (like <c>Trims_Changed</c>). A change the model already has (the binding following it, or this handler snapping the
    /// box back) does nothing, so there is no loop. Otherwise the modifier keys at that moment pick Toggle / Range / AddRange, and the box
    /// is set to what the model then says (a Shift click on a selected row keeps it selected).
    /// </summary>
    private void Select_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || App == null) return;
        bool want = box.IsChecked == true;
        var m = Keyboard.Modifiers;
        bool ctrl = (m & ModifierKeys.Control) != 0, shift = (m & ModifierKeys.Shift) != 0;
        var gesture = shift ? (ctrl ? SelectGesture.AddRange : SelectGesture.Range) : SelectGesture.Toggle;
        bool now;
        switch (box.DataContext)
        {
            case ClipRow c:
                if (_clipSel.Contains(c) == want) return;
                _clipSel.Apply(c, gesture, VisibleClips);
                now = _clipSel.Contains(c);
                break;
            case ScreenshotRow s:
                if (_shotSel.Contains(s) == want) return;
                _shotSel.Apply(s, gesture, _shots);
                now = _shotSel.Contains(s);
                break;
            default: return;
        }
        if (box.IsChecked != now) box.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, now);   // comes back here, finds the model equal, stops
    }

    /// <summary>Clear by the user (the Clear button, Esc): not while copies are being saved (the bar holds the Cancel); keyboard focus then goes into the list.</summary>
    private void ClearByUser()
    {
        if (LongRunning) return;
        bool moveFocus = SelectBar.IsKeyboardFocusWithin || !FocusInActiveList();
        ClearSelections();
        if (moveFocus) Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusActiveList);   // after the bar collapsed
    }

    private bool FocusInActiveList() => Keyboard.FocusedElement is UIElement f && f.IsVisible && ActiveList.IsAncestorOf(f);

    /// <summary>The first visible row checkbox of the list on screen, else the checked level segment (the bar that held the focus is gone).</summary>
    private void FocusActiveList()
    {
        foreach (var box in Descendants<CheckBox>(ActiveList))
            if (box.IsVisible && box.IsEnabled && box.Focusable && box.Focus()) return;
        (_level == LibraryLevel.Shared ? SharedLevel : LocalLevel).Focus();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>Sets a live-region TextBlock's text; when it changed, screen readers are told (same as the main window's).</summary>
    private static void SetLive(TextBlock tb, string text)
    {
        if (tb.Text == text) return;
        tb.Text = text;
        if (text.Length == 0 || !tb.IsVisible || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        try { (UIElementAutomationPeer.FromElement(tb) ?? UIElementAutomationPeer.CreatePeerForElement(tb))?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged); }
        catch (Exception ex) { Log.Warn("live region: " + ex.Message); }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || App == null) return;
        if (e.Key == Key.Escape && SelectionActive && !LongRunning)
        {
            e.Handled = true;
            ClearByUser();
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && ActiveList.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            if (OnShots) _shotSel.SelectAll(_shots); else _clipSel.SelectAll(VisibleClips);
        }
    }

    private Control ActiveList => OnShots ? ShotList : _level == LibraryLevel.Shared ? ClipSharedList : ClipLocalList;

    private void SelectClear_Click(object sender, RoutedEventArgs e) => ClearByUser();

    // ------------------------------------------------------------------ the bar

    private void SetBusy(BarBusy busy)
    {
        _busy = busy;
        UpdateBar();
    }

    /// <summary>Shows a status line (a live region: screen readers hear it change) without the clear-after timer.</summary>
    private void ShowStatus(string text)
    {
        SelectStatus.Visibility = Visibility.Visible;   // visible first: a hidden element raises no live-region event
        SetLive(SelectStatus, text);
    }

    private void SetBarStatus(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            SelectStatus.Visibility = Visibility.Collapsed;
            SelectStatus.Text = "";
            return;
        }
        ShowStatus(text);
        if (_busy != BarBusy.None) return;
        _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (SelectStatus.Text == text && _busy == BarBusy.None) SetBarStatus(null); }));
    }

    /// <summary>
    /// The bar follows the selection, except that it stays while copies are being saved (<see cref="BulkActions.BarVisible"/>): it then
    /// holds the only Cancel, Clear is off and Esc does nothing. Switching to the other feature meanwhile empties the selection but keeps
    /// the bar ("Saving copies…" and Cancel) until the save ends. (The Local | Shared row is replaced by the bar, so it cannot be used then.)
    /// </summary>
    private void UpdateBar()
    {
        bool shots = OnShots;
        int count = shots ? _shotSel.Count : _clipSel.Count;
        bool saving = LongRunning;
        bool active = BulkActions.BarVisible(count, saving);
        SelectBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        LevelRow.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        SelectClearButton.IsEnabled = !saving;
        if (!active)
        {
            _barSignature = null;
            _barButtons.Clear();
            SetBarStatus(null);
            return;
        }
        SetLive(SelectCount, count > 0 ? BulkActions.CountLabel(count, shots) : _busy == BarBusy.Uploading ? "Uploading…" : "Saving copies…");
        int linked = shots ? _shotSel.InOrder(_shots).Count(r => r.HasLink) : _clipSel.InOrder(VisibleClips).Count(r => r.HasLink);
        bool hasToken = App.HasCopiumToken, cloudOn = App.Settings.CloudSharingEnabled;
        // Rebuilt only when what it offers changes (a refresh must not take the keyboard focus off a bar button). Working / idle only
        // switches the buttons on and off; saving replaces them with Cancel, which is the one rebuild a busy state needs.
        string signature = $"{shots}|{_level}|{hasToken}|{cloudOn}|{linked == count}|{linked > 0}|{count > BulkActions.MaxShareFiles}|{_busy is BarBusy.Saving}|{_busy is BarBusy.Uploading}";
        if (signature != _barSignature)
        {
            _barSignature = signature;
            bool hadFocus = SelectActions.IsKeyboardFocusWithin;
            _barButtons.Clear();
            while (SelectActions.Children.Count > 1) SelectActions.Children.RemoveAt(1);
            foreach (var b in BarButtons(shots, count, linked, hasToken, cloudOn)) SelectActions.Children.Add(b);
            if (hadFocus) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { foreach (var (b, _) in _barButtons) if (b.IsEnabled && b.Focus()) break; });
        }
        foreach (var (button, enabled) in _barButtons) button.IsEnabled = enabled && _busy != BarBusy.Working;
    }

    private Button BarButton(string text, Action click, string? tip = null, bool enabled = true, string? style = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 6, 2), IsEnabled = enabled && _busy != BarBusy.Working };
        if (style != null) b.Style = (Style)FindResource(style);
        if (tip != null) { b.ToolTip = tip; ToolTipService.SetShowOnDisabled(b, true); }
        b.Click += (_, _) => click();
        _barButtons.Add((b, enabled));
        return b;
    }

    private IEnumerable<Button> BarButtons(bool shots, int count, int linked, bool hasToken, bool cloudOn)
    {
        if (_busy == BarBusy.Saving)
        {
            yield return BarButton("Cancel", () => _saveCts?.Cancel(), "Stop saving; copies already made stay");
            yield break;
        }
        if (_busy == BarBusy.Uploading)
        {
            yield return BarButton("Cancel", StopUpload, "Stop after the current upload; links already made stay");
            yield break;
        }
        bool tooMany = count > BulkActions.MaxShareFiles;
        if (shots)
        {
            if (BulkActions.UploadOffered(hasToken, cloudOn, count, linked))
                yield return BarButton(BulkActions.UploadLabel, () => Run(UploadSelectedAsync()), BulkActions.UploadTip(true), style: "AccentButton");
            if (cloudOn) yield return BarButton("Share files…", () => Run(ShareFilesAsync()), tooMany ? BulkActions.TooManyToShareText : "Drag or copy the files into Discord, a browser or a folder", !tooMany);
            if (linked == count) yield return BarButton("Copy links", BulkCopyLinks, "Copy the Copium links, one per line");
            yield return BarButton("Delete", () => Run(DeleteSelectedAsync()), "Move the selected files to the Recycle Bin", style: "DangerTextButton");
            yield break;
        }
        if (_level == LibraryLevel.Shared)
        {
            if (linked > 0) yield return BarButton("Copy links", BulkCopyLinks, "Copy the Copium links, one per line");
            yield break;
        }
        var labels = BulkActions.ShareButtonLabels(hasToken, cloudOn, count, linked);
        for (int i = 0; i < labels.Count; i++)
        {
            string label = labels[i];
            string? style = i == 0 ? "AccentButton" : null;   // the first share button is the main one
            if (label == BulkActions.SplitMainLabel)
            {
                // the split button: "Share" opens the share window (Share files…), "▾" the menu with every option
                Button? mainButton = null, arrowButton = null;
                void OpenMenu()
                {
                    if (BulkActions.ShareMenu(hasToken, cloudOn, count, linked, uploadOn: true) is { } items)
                        ShareMenu.OpenBulk(arrowButton!, items, RunBarAction, below: mainButton);   // drops from the whole split button
                }
                mainButton = BarButton(label, () => RunBarAction(BulkActions.SplitMainAction),
                    tooMany ? BulkActions.TooManyToShareText : BulkActions.SplitMainTip, BulkActions.SplitEnabled(count).Main, "AccentSplitMain");
                arrowButton = BarButton(ShareChoices.SplitArrowLabel, OpenMenu, ShareChoices.SplitArrowTip, BulkActions.SplitEnabled(count).Arrow, "AccentSplitArrow");
                mainButton.Margin = new Thickness(0, 0, 0, 2);
                mainButton.MinWidth = 52;
                arrowButton.Padding = new Thickness(7, 5, 7, 5);
                System.Windows.Automation.AutomationProperties.SetName(mainButton, BulkActions.SplitMainName);
                System.Windows.Automation.AutomationProperties.SetName(arrowButton, ShareChoices.SplitArrowName);
                mainButton.PreviewKeyDown += (_, e) => { if (ShareMenu.IsOpenKey(e)) { e.Handled = true; OpenMenu(); } };
                yield return mainButton;
                yield return arrowButton;
            }
            else if (label == BulkActions.ShareFilesLabel)
                yield return BarButton(label, () => Run(ShareFilesAsync()), tooMany ? BulkActions.TooManyToShareText : "Drag or copy the files into Discord, a browser or a folder", !tooMany, style);
            else
                yield return BarButton(label, BulkCopyLinks, "Copy the Copium links, one per line", style: style);
        }
        yield return BarButton("Delete", () => Run(DeleteSelectedAsync()), "Move the selected clips to the Recycle Bin", style: "DangerTextButton");
    }

    private void RunBarAction(BulkAction action)
    {
        switch (action)
        {
            case BulkAction.Upload: Run(UploadSelectedAsync()); break;
            case BulkAction.CopyLinks: BulkCopyLinks(); break;
            case BulkAction.ShareFiles: Run(ShareFilesAsync()); break;
            case BulkAction.SaveCopies: Run(SaveCopiesAsync()); break;
            case BulkAction.Delete: Run(DeleteSelectedAsync()); break;
        }
    }

    private async void Run(Task task)
    {
        try { await task; }
        catch (Exception ex)
        {
            Log.Error("library: bulk action", ex);
            _saveCts = null;
            _uploadCts = null;
            SetBusy(BarBusy.None);
            SetBarStatus("Something went wrong: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ copy links

    private void BulkCopyLinks()
    {
        var links = OnShots ? _shotSel.InOrder(_shots).Select(r => r.Link).ToList() : _clipSel.InOrder(VisibleClips).Select(r => r.Link).ToList();
        string text = BulkActions.JoinLinks(links);
        int n = text.Length == 0 ? 0 : text.Split("\r\n").Length;
        if (n == 0) { SetBarStatus("There are no links to copy"); return; }
        App.CopyToClipboard(text);
        SetBarStatus(n == 1 ? "1 link copied" : $"{n} links copied");
    }

    // ------------------------------------------------------------------ share files

    private async Task ShareFilesAsync()
    {
        if (_busy != BarBusy.None) return;
        bool onShots = OnShots;   // the feature when the button was pressed (the user may switch while the files are looked for)
        SetBusy(BarBusy.Working);
        SetBarStatus("Looking for the files…");
        string? status = null;
        try
        {
            List<ShareFileItem> items;
            int total;
            if (onShots)
            {
                var rows = _shotSel.InOrder(_shots);
                total = rows.Count;
                var paths = rows.Select(r => (r.Entry.Path, r.Entry.Size)).ToList();
                var shots = App.Screenshots;
                // the path rule and the file check off the UI thread (a screenshots folder may be on a network drive)
                items = await Task.Run(() => paths.Where(p => shots.IsOpenableEntry(p.Path)).Select(p => new ShareFileItem(p.Path, false, p.Size, 0)).ToList());
            }
            else
            {
                // No per-row file probe here (N archived clips on a dropped share would wait out N timeouts one after the other): the
                // share window looks at all the files together on a worker and says which could not be reached.
                var rows = _clipSel.InOrder(VisibleClips);
                total = rows.Count;
                items = rows.Where(r => !r.IsDeleting).Select(r => new ShareFileItem(r.Entry.Path, r.Entry.IsArchived, r.Entry.Size, r.Entry.Duration)).ToList();
            }
            if (items.Count == 0) status = BulkActions.ShareFilesSummary(0, Math.Max(total, 1)) ?? "None of the files could be reached";
            else if (items.Count > BulkActions.MaxShareFiles) status = BulkActions.TooManyToShareText;
            else if (!App.OpenShareFilesWindow(items, onShots)) status = AppController.ShareWindowFailedText;
            else status = BulkActions.ShareFilesSummary(items.Count, total);
        }
        finally { SetBusy(BarBusy.None); }
        SetBarStatus(status);   // after leaving Working, so the status clears on its timer
    }

    // ------------------------------------------------------------------ save copies

    private async Task SaveCopiesAsync()
    {
        if (_busy != BarBusy.None || OnShots || _level != LibraryLevel.Local) return;
        var rows = _clipSel.InOrder(VisibleClips);
        // No per-row file probe (it would wait out an SMB timeout per archived clip): a row that is uploading or being deleted is
        // left out as busy; a missing file is found by the save itself, on its worker, and counted in its summary.
        var ready = rows.Where(r => !r.IsDeleting && !r.IsUploading).ToList();
        int busyRows = rows.Count - ready.Count;
        if (ready.Count == 0)
        {
            SetBarStatus(BulkActions.SaveSummary(0, Math.Max(rows.Count, 1), null, 0, busyRows));
            return;
        }
        using var cts = new CancellationTokenSource();
        _saveCts = cts;
        int total = ready.Count;
        ClipRow? current = null;   // the row the save owns now (only it is marked and later cleared)
        bool finished = false;
        void EndRow(ClipRow row) { row.Progress = 0; row.Status = null; row.IsSaving = false; row.IsUploading = false; }
        // A row's turn (UI thread, before anything of it is copied): a single upload / save / delete the user started on it after the
        // bulk save began makes it busy, and it is skipped (counted as busy) and left alone; otherwise the save takes it.
        bool BeginRow(int i)
        {
            if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => BeginRow(i));
            if (current != null) { EndRow(current); current = null; }
            if (finished || i < 0 || i >= total) return false;
            var row = ready[i];
            if (row.IsUploading || row.IsSaving || row.IsDeleting || !_local.Contains(row)) return false;   // busy, or gone from the list meanwhile
            current = row;
            row.IsProblem = false;
            row.IsSaving = true;
            row.IsUploading = true;
            ShowStatus($"Saving {i + 1} of {total}…");
            return true;
        }
        void OnProgress(int i, double p) => Dispatcher.BeginInvoke(() =>
        {
            if (finished || i < 0 || i >= total) return;
            var row = ready[i];
            if (!ReferenceEquals(current, row)) return;   // not a row the save owns (skipped as busy), or a late report
            row.Progress = p;
            row.Status = $"Saving… {p:P0}";
        });
        SetBusy(BarBusy.Saving);
        ShowStatus($"Saving 1 of {total}…");
        BulkSaveResult result;
        try { result = await App.SaveCopiesAsync(ready.Select(r => r.Entry).ToList(), Window.GetWindow(this), OnProgress, cts.Token, BeginRow); }
        catch (Exception ex)
        {
            Log.Error("library: save copies", ex);
            result = new BulkSaveResult(0, total, null, ex.Message, false);
        }
        finally
        {
            finished = true;
            if (current != null) EndRow(current);
            _saveCts = null;
        }
        SetBusy(BarBusy.None);
        string? status = result.Cancelled
            ? (result.Folder == null ? null : $"Cancelled. {result.Saved} of {result.Total + busyRows} saved.")
            : BulkActions.SaveSummary(result.Saved, result.Total + busyRows, result.FirstError, result.Unreachable, busyRows + result.Busy);
        SetBarStatus(status);
    }

    // ------------------------------------------------------------------ upload and copy links

    /// <summary>The bar's Cancel while uploading: the batch stops after the item in hand (an upload under way is not aborted).</summary>
    private void StopUpload()
    {
        if (_uploadCts == null || _uploadStopping) return;
        _uploadStopping = true;
        try { _uploadCts.Cancel(); } catch (ObjectDisposedException) { }
        ShowStatus(BulkActions.UploadStoppingText);
    }

    private Task UploadSelectedAsync() => OnShots ? UploadShotsAsync() : UploadClipsAsync();

    private string UploadLine(int index, int total, double fraction = -1) =>
        _uploadStopping ? BulkActions.UploadStoppingText : BulkActions.UploadingStatus(index, total, fraction);

    private async Task UploadClipsAsync()
    {
        if (_busy != BarBusy.None || OnShots || _level != LibraryLevel.Local) return;
        var rows = _clipSel.InOrder(VisibleClips).ToList();
        if (rows.Count == 0) return;
        using var cts = new CancellationTokenSource();
        _uploadCts = cts;
        _uploadStopping = false;
        int total = rows.Count;
        var index = new Dictionary<ClipEntry, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < total; i++) index[rows[i].Entry] = i;
        ClipRow? current = null;   // the row the upload owns now (only it is marked and later cleared)
        bool finished = false;
        void EndRow(ClipRow row) { row.Progress = 0; row.Status = null; row.IsUploading = false; }
        // A row's turn (UI thread, before its upload): a single upload / save / delete the user started on it meanwhile makes it busy;
        // it is skipped (counted as busy) and left alone. Otherwise the upload takes it.
        bool BeginRow(int i)
        {
            if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => BeginRow(i));
            if (current != null) { EndRow(current); current = null; }
            if (finished || i < 0 || i >= total) return false;
            var row = rows[i];
            if (row.IsUploading || row.IsSaving || row.IsDeleting || !_local.Contains(row)) return false;   // busy, or gone from the list meanwhile
            current = row;
            row.IsProblem = false;
            row.Progress = 0;
            row.IsUploading = true;
            row.Status = "Uploading…";
            ShowStatus(UploadLine(i + 1, total));
            return true;
        }
        IProgress<double>? ProgressFor(ClipEntry entry)
        {
            int i = index.TryGetValue(entry, out int n) ? n : -1;
            return new Progress<double>(p =>   // made on the UI thread: reports there
            {
                if (finished || i < 0 || !ReferenceEquals(current, rows[i])) return;   // not a row the upload owns, or a late report
                rows[i].Progress = p;
                rows[i].Status = $"Uploading… {p:P0}";
                ShowStatus(UploadLine(i + 1, total, p));
            });
        }
        SetBusy(BarBusy.Uploading);
        ShowStatus(UploadLine(1, total));
        BulkShareResult result;
        try { result = await App.ShareManyAsync(rows.Select(r => r.Entry).ToList(), BeginRow, ProgressFor, cts.Token); }
        catch (Exception ex)
        {
            Log.Error("library: upload", ex);
            result = new BulkShareResult(0, 0, total, Array.Empty<string>(), 0, 0, 0, ex.Message, false, false);
        }
        finally
        {
            finished = true;
            if (current != null) EndRow(current);
            _uploadCts = null;
            _uploadStopping = false;
        }
        SetBusy(BarBusy.None);
        foreach (var row in rows) row.Refresh();
        if (result.Uploaded > 0) Refresh();   // the clips are shared now: they join the Shared tab and get the cloud icon
        SetBarStatus(BulkActions.UploadStatus(result, false));
    }

    private async Task UploadShotsAsync()
    {
        if (_busy != BarBusy.None || !OnShots) return;
        var rows = _shotSel.InOrder(_shots).ToList();
        if (rows.Count == 0) return;
        using var cts = new CancellationTokenSource();
        _uploadCts = cts;
        _uploadStopping = false;
        int total = rows.Count;
        var index = new Dictionary<ScreenshotEntry, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < total; i++) index[rows[i].Entry] = i;
        ScreenshotRow? current = null;
        bool finished = false;
        void EndRow(ScreenshotRow row) { row.Status = null; row.IsUploading = false; }
        bool BeginRow(int i)
        {
            if (!Dispatcher.CheckAccess()) return Dispatcher.Invoke(() => BeginRow(i));
            if (current != null) { EndRow(current); current = null; }
            if (finished || i < 0 || i >= total) return false;
            var row = rows[i];
            if (row.IsUploading || row.IsDeleting || !_shots.Contains(row)) return false;   // busy, or gone from the list meanwhile
            current = row;
            row.IsProblem = false;
            row.IsUploading = true;
            row.Status = "Uploading…";
            ShowStatus(UploadLine(i + 1, total));
            return true;
        }
        IProgress<double>? ProgressFor(ScreenshotEntry entry)
        {
            int i = index.TryGetValue(entry, out int n) ? n : -1;
            return new Progress<double>(p =>
            {
                if (finished || i < 0 || !ReferenceEquals(current, rows[i])) return;
                rows[i].Status = $"Uploading… {p:P0}";
                ShowStatus(UploadLine(i + 1, total, p));
            });
        }
        SetBusy(BarBusy.Uploading);
        ShowStatus(UploadLine(1, total));
        BulkShareResult result;
        try { result = await App.UploadShotsAsync(rows.Select(r => r.Entry).ToList(), BeginRow, ProgressFor, cts.Token); }
        catch (Exception ex)
        {
            Log.Error("library: screenshot upload", ex);
            result = new BulkShareResult(0, 0, total, Array.Empty<string>(), 0, 0, 0, ex.Message, false, false);
        }
        finally
        {
            finished = true;
            if (current != null) EndRow(current);
            _uploadCts = null;
            _uploadStopping = false;
        }
        SetBusy(BarBusy.None);
        foreach (var row in rows) row.Refresh();
        if (result.Uploaded > 0 && OnShots) RefreshShots();
        SetBarStatus(BulkActions.UploadStatus(result, true));
    }

    // ------------------------------------------------------------------ delete

    private Task DeleteSelectedAsync() => OnShots ? DeleteSelectedShotsAsync() : DeleteSelectedClipsAsync();

    /// <summary>
    /// Runs a bulk delete with the bar busy from the first moment (the checks, the confirmation, the deletes): a second Delete (a double
    /// click, key repeat, the share menu) is ignored and the bar's buttons are off until it ends, however it ends. The returned text is
    /// shown on the bar afterwards (so it clears on its timer).
    /// </summary>
    private async Task RunDelete(Func<Task<string?>> delete)
    {
        if (_busy != BarBusy.None) return;
        SetBusy(BarBusy.Working);
        string? status;
        try { status = await delete(); }
        finally { SetBusy(BarBusy.None); }
        if (status != null) SetBarStatus(status);
    }

    private nint OwnerHandle(Window? owner)
    {
        try { if (owner != null) return new System.Windows.Interop.WindowInteropHelper(owner).Handle; }
        catch { }
        return 0;
    }

    private Task DeleteSelectedClipsAsync() => _level != LibraryLevel.Local ? Task.CompletedTask : RunDelete(DeleteClipsCoreAsync);

    private async Task<string?> DeleteClipsCoreAsync()
    {
        var rows = _clipSel.InOrder(VisibleClips).Where(r => r.CanDelete).ToList();
        if (rows.Count == 0) return null;
        var deleter = NewDeleter(rows);
        var blocked = new Dictionary<ClipRow, ClipDeleteResult>(ReferenceEqualityComparer.Instance);
        foreach (var r in rows)
        {
            if (r.IsUploading) { blocked[r] = ClipDeleteResult.Uploading; continue; }
            if (deleter.Precheck(r.Entry) is { } refused) blocked[r] = refused;
        }
        ClipDeleteResult? first = blocked.Count > 0 ? blocked.Values.First() : null;
        var ok = rows.Where(r => !blocked.ContainsKey(r)).ToList();
        if (ok.Count == 0)
        {
            foreach (var (row, why) in blocked) row.SetProblem(ClipDeleter.ProblemText(why));
            return ClipDeleter.BulkProblemText(0, rows.Count, first);
        }
        int unselectedTrims = ok.Where(r => r.IsParent).Sum(p => ClipGrouping.TrimsOf(_localAll, r => r.Entry, p).Count(t => !_clipSel.Contains(t)));
        string body = ClipDeleter.BulkConfirmBody(ok.Count, ok.Count(r => r.IsShared), unselectedTrims, blocked.Count, false);
        var owner = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        var dialog = new ClipDeleteConfirmWindow(ClipDeleter.BulkConfirmTitle(ok.Count, false), body, "Delete", owner);
        TestGuard.CheckLaunch("the delete confirm window");
        if (dialog.ShowDialog() != true || !dialog.Confirmed) return null;
        nint handle = OwnerHandle(owner);
        SetBarStatus("Deleting…");
        int deleted = 0;
        var kept = new List<(ClipRow Row, ClipDeleteResult Why)>();
        var byEntry = ok.ToDictionary(r => r.Entry, r => r, ReferenceEqualityComparer.Instance as IEqualityComparer<ClipEntry>);
        foreach (var entry in ClipDeleter.BulkOrder(ok.Select(r => r.Entry).ToList()))
        {
            var row = byEntry[entry];
            if (!VisibleClips.Contains(row)) continue;   // gone meanwhile (counts as neither deleted nor kept)
            if (row.IsDeleting) continue;   // a single-row delete is running on it: not ours to touch or clear (counted as kept by the total)
            row.IsDeleting = true;
            var result = ClipDeleteResult.Failed;
            try { result = await deleter.DeleteAsync(entry, handle); }
            catch (Exception ex) { Log.Warn("library: clip delete: " + ex.Message); }
            finally { row.IsDeleting = false; }
            if (result == ClipDeleteResult.Deleted) deleted++;
            else { kept.Add((row, result)); first ??= result; }
        }
        _clipSel.Clear();   // a finished delete ends the selection
        Refresh();
        foreach (var (row, why) in kept.Concat(blocked.Select(b => (Row: b.Key, Why: b.Value)))) row.SetProblem(ClipDeleter.ProblemText(why));
        string problem = ClipDeleter.BulkProblemText(deleted, rows.Count, first);
        App.Say(problem.Length == 0 ? (deleted == 1 ? "Deleted 1 clip" : $"Deleted {deleted} clips") : problem);
        return null;
    }

    private Task DeleteSelectedShotsAsync() => RunDelete(DeleteShotsCoreAsync);

    private async Task<string?> DeleteShotsCoreAsync()
    {
        var rows = _shotSel.InOrder(_shots);
        if (rows.Count == 0) return null;
        // What can go is decided before anything is asked (ClipDeleter.PlanShotDelete). A row without a file is never part of it: the
        // delete would only drop its list entry (its Copium link record with it); that is Remove from list, row by row.
        var snapshot = rows.Select(r => (Path: r.Entry.Path, r.HasLocalFile, r.IsUploading, r.IsDeleting)).ToList();
        var screenshots = App.Screenshots;
        // the path rule, the drive type and the file itself off the UI thread (a screenshots folder may be on a network drive). A file
        // deleted outside Clink since the list was loaded counts as no file; the disk is only looked at for a path the rule allows.
        var facts = await Task.Run(() =>
        {
            var folders = screenshots.KnownFolders();
            return snapshot.Select(s =>
            {
                bool rule = s.HasLocalFile && ScreenshotService.IsOpenable(s.Path, folders);
                bool hasFile = s.HasLocalFile && (!rule || System.IO.File.Exists(s.Path));
                return new ClipDeleter.ShotFacts(hasFile, s.IsUploading, s.IsDeleting,
                    hasFile && rule && ScreenshotService.IsOpenableFile(s.Path, folders), hasFile && ScreenshotService.IsOnLocalFixedDrive(s.Path));
            }).ToList();
        });
        var plan = ClipDeleter.PlanShotDelete(facts);
        var ok = plan.Delete.Select(i => rows[i]).ToList();
        int withoutFile = plan.Count(ClipDeleter.ShotSkip.NoFile);
        int blocked = plan.Skipped.Count(s => s.Why != ClipDeleter.ShotSkip.NoFile);
        void MarkSkipped()
        {
            foreach (var (index, why) in plan.Skipped)
                if (ClipDeleter.ShotSkipText(why) is { Length: > 0 } text) rows[index].SetProblem(text);
        }
        if (ok.Count == 0)
        {
            MarkSkipped();
            return withoutFile > 0 && blocked == 0 ? "Nothing was deleted. " + ClipDeleter.WithoutFileText(withoutFile)
                : plan.Count(ClipDeleter.ShotSkip.Uploading) == blocked && blocked > 0 ? "Nothing was deleted: the screenshots are uploading."
                : "Nothing was deleted: the screenshots can't be deleted right now.";
        }
        string body = ClipDeleter.BulkConfirmBody(ok.Count, ok.Count(r => r.HasLink), 0, blocked, true, withoutFile);
        var owner = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        var dialog = new ClipDeleteConfirmWindow(ClipDeleter.BulkConfirmTitle(ok.Count, true), body, "Delete", owner);
        TestGuard.CheckLaunch("the delete confirm window");
        if (dialog.ShowDialog() != true || !dialog.Confirmed) return null;
        nint handle = OwnerHandle(owner);
        SetBarStatus("Deleting…");
        int deleted = 0;
        var failed = new List<(ScreenshotRow Row, ScreenshotDeleteResult Why)>();
        var noFile = new List<ScreenshotRow>();
        foreach (var row in ok)
        {
            if (!_shots.Contains(row) || row.IsUploading || row.IsDeleting) continue;   // gone, or busy now (a single-row delete is not ours to touch or clear)
            if (!row.HasLocalFile) { noFile.Add(row); continue; }   // its file went meanwhile (the list was refreshed): never part of a bulk delete
            row.IsDeleting = true;
            var result = ScreenshotDeleteResult.Failed;
            // requireFile: a file that is gone now (deleted outside Clink while the confirmation was up) keeps its entry and its link record
            try { result = await App.Screenshots.DeleteAsync(row.Entry, handle, requireFile: true); }
            catch (Exception ex) { Log.Warn("library: screenshot delete: " + ex.Message); }
            finally { row.IsDeleting = false; }
            if (result == ScreenshotDeleteResult.FileGone) { noFile.Add(row); continue; }
            if (result is ScreenshotDeleteResult.Deleted or ScreenshotDeleteResult.ListNotUpdated) deleted++;
            if (result != ScreenshotDeleteResult.Deleted) failed.Add((row, result));
        }
        _shotSel.Clear();
        if (OnShots) RefreshShots();
        foreach (var (row, why) in failed)
            row.SetProblem(ScreenshotRow.DeleteProblemText(why, ScreenshotService.IsOnLocalFixedDrive(row.Entry.Path)));
        foreach (var row in noFile) row.SetProblem(ScreenshotRow.NoFileText);
        MarkSkipped();
        App.Say(ClipDeleter.ShotBulkSummary(deleted, rows.Count, withoutFile + noFile.Count));   // a file found gone at delete time counts as skipped, without a file
        return null;
    }
}