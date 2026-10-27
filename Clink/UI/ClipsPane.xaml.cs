using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Clink.Screenshots;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The library pane (docs/SPEC-v2.md §1). Despite its name (from when it only listed clips) it lists both stores: a big top switch
/// Clips | Screenshots, under it a smaller Local | Shared switch, and on the clips side a Trimmed filter chip (<see cref="LibraryView"/>
/// holds the rules). Clips: Local = every local file (with shared / trim / local-only / archived icons), Shared = everything shared,
/// listed by link even after the local file is gone. Screenshots: Local = history entries whose file is there, Shared = entries with a
/// Copium link. Rows come from <see cref="AppController.Library"/> / the screenshot history; thumbnails and file checks run on a
/// worker so an offline archive never stalls the window. Share works here; Trim asks the window (<see cref="TrimRequested"/>) so the
/// editor gets the right owner.
/// </summary>
public partial class ClipsPane : UserControl
{
    /// <summary>An archive folder that did not answer is not asked again for this long (each try can block for the SMB timeout).</summary>
    private static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(30);

    private readonly ObservableCollection<ClipRow> _local = new(), _shared = new();
    private readonly ObservableCollection<ScreenshotRow> _shots = new();
    private readonly Dictionary<string, ScreenshotRow> _shotRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<ScreenshotRow> _thumbQueue = new();
    private ScreenshotListModel? _shotModel;
    private ScreenshotService? _shotService;   // the one whose history events this pane listens to
    private List<ScreenshotItem> _shotItems = new();
    private int _shotGen;
    private bool _thumbLoop, _shotsLoaded;
    // the user's choice (the effective feature also depends on which features are on): restored from the settings on the first look
    private LibraryFeature _wantedFeature;
    private LibraryLevel _level;
    private bool _stateRestored, _syncing, _trimmedChip;
    private readonly Dictionary<ClipEntry, ClipRow> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, DateTime> _offlineDirs = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshGen;

    public ClipsPane()
    {
        InitializeComponent();
        ClipLocalList.ItemsSource = _local;
        ClipSharedList.ItemsSource = _shared;
        ShotList.ItemsSource = _shots;
        // The row a press went down on is good for the release that follows it and no other. A row's own button handles its release
        // (Click), so the row never hears of it and the press would stay remembered: a later press that began somewhere else (the
        // empty part of the list, the tab header) and came up over that row would then play it. So every release in the pane forgets
        // the press once its handlers have had their turn (this one runs last, handled or not), every press starts afresh, and a
        // pointer that leaves the pane with the button down has not clicked anything.
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _pressed = null), handledEventsToo: true);
        PreviewMouseLeftButtonDown += (_, _) => _pressed = null;
        MouseLeave += (_, _) => _pressed = null;
        // Shown (window back from the tray, Clips tab opened, layout switched): re-read, so files deleted meanwhile drop out and an
        // archive that is back gets looked at again. Hidden: the thumbnail worker stops; nothing is extracted while a game runs.
        IsVisibleChanged += (_, a) =>
        {
            if ((bool)a.NewValue) Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });   // not inside the tree change that showed it
            else _refreshGen++;
        };
    }

    /// <summary>The user wants to trim this clip: the window opens the editor.</summary>
    internal event Action<ClipEntry>? TrimRequested;

    /// <summary>The user clicked a clip (its row or its thumbnail): the window opens the player.</summary>
    internal event Action<ClipEntry>? PlayRequested;

    /// <summary>The user picked another feature or level: the window saves the choice (<see cref="Settings.LibraryFeature"/>, <see cref="Settings.LibraryLevel"/>).</summary>
    internal event Action? ViewChanged;

    private static AppController App => AppController.Instance;

    internal LibraryLevel Level => _level;

    /// <summary>The feature the user chose (what is saved), which may differ from <see cref="Feature"/> while the other one is switched off.</summary>
    internal LibraryFeature WantedFeature => _wantedFeature;

    /// <summary>The feature shown: the user's choice, or the one that is on when the other was switched off.</summary>
    internal LibraryFeature Feature => LibraryView.Fallback(_wantedFeature, App.Settings.ClipsEnabled, App.Settings.ScreenshotsEnabled);

    /// <summary>Cloud sharing was switched: every row shows or hides its Share / Upload button (a row with a link keeps "Copy link").</summary>
    public void RefreshShareButtons()
    {
        foreach (var row in _rows.Values.ToList()) row.Refresh();
        foreach (var row in _shotRows.Values.ToList()) row.Refresh();
    }

    // ------------------------------------------------------------------ the two switches

    /// <summary>First look: the saved feature and level (the saved feature falls back at every refresh, see <see cref="Feature"/>).</summary>
    private void RestoreState()
    {
        if (_stateRestored) return;
        _stateRestored = true;
        var s = App.Settings;
        (_wantedFeature, _level) = LibraryView.Resolve(s.LibraryFeature, s.LibraryLevel, clipsOn: true, screenshotsOn: true);
    }

    /// <summary>Brings the switches in line with the choice and the feature settings (both rows, live): which segments are checked, the top row hidden when one feature is on, the Trimmed chip on the clips side only.</summary>
    private void SyncSwitches()
    {
        var s = App.Settings;
        var feature = Feature;
        _syncing = true;
        try
        {
            ClipsFeature.IsChecked = feature == LibraryFeature.Clips;
            ShotsFeature.IsChecked = feature == LibraryFeature.Screenshots;
            LocalLevel.IsChecked = _level == LibraryLevel.Local;
            SharedLevel.IsChecked = _level == LibraryLevel.Shared;
            TrimmedChip.IsChecked = _trimmedChip;
        }
        finally { _syncing = false; }
        FeatureBar.Visibility = LibraryView.ShowFeatureSwitch(s.ClipsEnabled, s.ScreenshotsEnabled) ? Visibility.Visible : Visibility.Collapsed;
        TrimmedChip.Visibility = LibraryView.ShowTrimmedChip(feature) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetLabels(int local, int shared)
    {
        string l = LibraryView.Label("Local", local), sh = LibraryView.Label("Shared", shared);
        LocalLevel.Content = l; LocalLevel.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, l);
        SharedLevel.Content = sh; SharedLevel.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, sh);
    }

    private void Feature_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || App == null) return;
        _wantedFeature = ReferenceEquals(sender, ShotsFeature) ? LibraryFeature.Screenshots : LibraryFeature.Clips;
        ViewChanged?.Invoke();
        Refresh();
    }

    private void Level_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || App == null) return;
        _level = ReferenceEquals(sender, SharedLevel) ? LibraryLevel.Shared : LibraryLevel.Local;
        ViewChanged?.Invoke();
        Refresh();
    }

    private void Trimmed_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || App == null) return;
        _trimmedChip = TrimmedChip.IsChecked == true;
        Refresh();
    }

    /// <summary>Which list is on screen and what the empty list says.</summary>
    private void ShowList(LibraryFeature feature, int count)
    {
        bool shots = feature == LibraryFeature.Screenshots, shared = _level == LibraryLevel.Shared;
        ClipLocalList.Visibility = !shots && !shared ? Visibility.Visible : Visibility.Collapsed;
        ClipSharedList.Visibility = !shots && shared ? Visibility.Visible : Visibility.Collapsed;
        ShotList.Visibility = shots ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = LibraryView.EmptyText(feature, _level, LibraryView.TrimmedOnly(feature, _trimmedChip));
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Re-reads the list on screen (the clip library or the screenshot history, per the switches). Rows for entries already shown keep their thumbnail and upload state.</summary>
    public void Refresh()
    {
        if (App == null) return;
        RestoreState();
        SyncSwitches();
        if (Feature == LibraryFeature.Screenshots) RefreshShots(); else RefreshClips();
    }

    private void RefreshClips()
    {
        _shotGen++;   // a screenshot read still under way is not wanted any more
        var entries = App.Library.Entries;
        var keep = new HashSet<ClipEntry>(ReferenceEqualityComparer.Instance);
        var all = new List<ClipRow>();
        var hasFileRows = new HashSet<ClipRow>();
        foreach (var e in entries)
        {
            // Archived entries are not File.Exists-checked on the UI thread (an offline share can block); the worker says "archive offline".
            bool exists = !e.IsArchived && e.Exists;
            if (!e.IsShared && !e.IsArchived && !exists) continue;   // the library drops these on load; a late delete shows up here
            var row = RowFor(e);
            keep.Add(e);
            if (!e.IsArchived && exists && !row.HasLocalFile && row.Thumbnail == null) row.Checked = false;   // back (Recycle Bin restore): make its thumbnail
            if (!e.IsArchived) row.HasLocalFile = exists;   // a shared clip deleted outside Clink: no Play / folder, "local file deleted"
            else if (row.Checked && !row.HasLocalFile) row.Checked = false;   // look again: the archive may be back (or the file deleted there)
            bool hasFile = e.IsArchived ? !row.DeletedFromArchive : exists;
            all.Add(row);
            if (hasFile) hasFileRows.Add(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k)).ToList()) _rows.Remove(gone);
        bool trimmed = LibraryView.TrimmedOnly(LibraryFeature.Clips, _trimmedChip);
        Func<ClipRow, bool> isLocal = hasFileRows.Contains, isShared = r => r.IsShared, isTrim = r => r.IsTrim;
        _localAll = LibraryView.Pick(all, LibraryLevel.Local, false, isLocal, isShared, isTrim);
        var local = trimmed ? LibraryView.Pick(all, LibraryLevel.Local, true, isLocal, isShared, isTrim) : _localAll;
        var shared = LibraryView.Pick(all, LibraryLevel.Shared, trimmed, isLocal, isShared, isTrim);
        ClipGrouping.CarryExpanded(_localAll, r => r.Entry, _lastClipKeys, _expandedClips);   // an archive move changes a path, not the open state
        // A trim that was not in the list before opens its clip, so the user sees it (the first look only remembers what is there).
        foreach (var key in ClipGrouping.TakeNewTrimParents(_localAll, r => r.Entry, _seenClips, seed: !_clipsSeeded)) _expandedClips.Add(key);
        _clipsSeeded = true;
        var localView = LocalView(local, trimmed);
        ClipGrouping.SyncRows(_local, localView);
        Replace(_shared, shared);
        SetLabels(local.Count, shared.Count);
        var shown = _level == LibraryLevel.Shared ? shared : localView;
        ShowList(LibraryFeature.Clips, shown.Count);
        // Only listed rows, in list order (newest first), and only while the pane is on screen (IsVisibleChanged refreshes on show).
        if (IsVisible) LoadThumbnails(shown.Where(r => !r.Checked).ToList());
    }

    // Local level: trims nested under the clip they were cut from (ClipGrouping). The Trimmed filter and the Shared level stay flat.
    private List<ClipRow> _localAll = new();
    private readonly HashSet<string> _seenClips = ClipGrouping.NewExpanded(), _expandedClips = ClipGrouping.NewExpanded();
    private bool _clipsSeeded;
    private readonly Dictionary<ClipRow, string> _lastClipKeys = new(ReferenceEqualityComparer.Instance as IEqualityComparer<ClipRow>);

    /// <summary>The rows the Local list shows: the Trimmed filter's flat list as it is, otherwise the nested list with collapsed parents' trims left out (so their thumbnails are not made either).</summary>
    private List<ClipRow> LocalView(List<ClipRow> filtered, bool trimmedOnly)
    {
        var flat = ClipGrouping.View(_localAll, filtered, trimmedOnly, r => r.Entry, _expandedClips);
        foreach (var f in flat) f.Item.SetNesting(f.Kind, f.ChildCount, f.Expanded);
        return flat.Select(f => f.Item).ToList();
    }

    /// <summary>A parent's chevron: show or hide its trims. Only the rows between change (the list keeps its scroll position and the chevron its focus).</summary>
    private void Trims_Changed(object sender, RoutedEventArgs e)
    {
        // Driven by the toggle's state (not Click), so mouse, Space and UI Automation's Toggle all take this path. The binding
        // setting the state from the model comes back here too, but then the state equals the model and nothing is done.
        var row = RowOf(sender);
        if (row == null || sender is not System.Windows.Controls.Primitives.ToggleButton toggle) return;
        bool chip = LibraryView.TrimmedOnly(LibraryFeature.Clips, _trimmedChip);
        if (!ClipGrouping.ShouldToggle(toggle.IsChecked, row.IsExpanded, row.IsParent, chip))
        {
            if (toggle.IsChecked != row.IsExpanded) toggle.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, row.IsExpanded);   // snap back to the model (the binding stays)
            return;
        }
        ClipGrouping.CarryExpanded(_localAll, r => r.Entry, _lastClipKeys, _expandedClips);   // an archive move may be queued: toggle the current key
        string key = ClipGrouping.Key(row.Entry.Path);
        if (!_expandedClips.Remove(key)) _expandedClips.Add(key);
        var view = LocalView(_localAll, false);   // the Trimmed chip returned above, so this is the nested list
        ClipGrouping.SyncRows(_local, view, int.MaxValue);   // a toggle is one contiguous block: never rebuild (the chevron keeps focus)
        if (IsVisible && _level == LibraryLevel.Local) LoadThumbnails(view.Where(r => !r.Checked).ToList());
    }

    /// <summary>
    /// The archive service reached its share again (at login the NAS may come up after Clink): forget which folders did not
    /// answer, so the rows found "archive offline" are looked at again now instead of after <see cref="OfflineRetry"/>.
    /// </summary>
    public void ArchiveIsBack()
    {
        _offlineDirs.Clear();
        Refresh();
    }

    private ClipRow RowFor(ClipEntry e)
    {
        if (!_rows.TryGetValue(e, out var row)) { row = new ClipRow(e); _rows[e] = row; }
        else
        {
            row.Refresh();
            // Same entry, different file: a trim with "Replace original" renamed the cut over it. Make the thumbnail again.
            if (row.Checked && row.CheckedSize != e.Size) { row.Thumbnail = null; row.Checked = false; }
        }
        return row;
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows) where T : class
    {
        if (target.Count == rows.Count && target.Zip(rows).All(p => ReferenceEquals(p.First, p.Second))) return;
        target.Clear();
        foreach (var r in rows) target.Add(r);
    }

    /// <summary>Thumbnails + "does the file exist" on a worker thread, newest first; results land on the UI thread.</summary>
    private void LoadThumbnails(List<ClipRow> rows)
    {
        if (rows.Count == 0) return;
        int gen = ++_refreshGen;
        Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (gen != _refreshGen) return;
                string path = row.Entry.Path;
                bool archived = row.Entry.IsArchived;
                long size = row.Entry.Size;
                var state = Probe(path, archived, useOfflineCache: true);
                BitmapSource? bmp = null;
                if (state == FileState.Present && row.Thumbnail == null) { try { bmp = Thumbnails.Get(path, 176, 100); } catch { } }
                Dispatcher.BeginInvoke(() =>
                {
                    if (!Apply(row, path, archived, state, fromAction: false)) return;
                    row.Checked = true;
                    row.CheckedSize = size;
                    if (bmp != null) row.Thumbnail = bmp;
                });
            }
        });
    }

    private enum FileState { Present, Missing, Offline }

    /// <summary>
    /// Is the file there? For an archived clip, whether its archive folder answers first: if not, the NAS / VPN is down
    /// (<see cref="FileState.Offline"/>); if it does and the file is not in it, it was deleted there (<see cref="FileState.Missing"/>,
    /// only on an explicit "file not found" with the folder still answering after it; any other error is Offline). A folder that
    /// did not answer is remembered for <see cref="OfflineRetry"/>, so the worker waits out the SMB timeout once, not once per clip.
    /// Any thread; never call it on the UI thread for an archived path.
    /// </summary>
    private FileState Probe(string path, bool archived, bool useOfflineCache)
    {
        static bool FileThere(string p) { try { return File.Exists(p); } catch { return false; } }
        static bool DirThere(string p) { try { return Directory.Exists(p); } catch { return false; } }
        string? dir = archived ? Path.GetDirectoryName(path) : null;
        if (dir == null) return FileThere(path) ? FileState.Present : FileState.Missing;
        if (useOfflineCache && _offlineDirs.TryGetValue(dir, out var since) && DateTime.UtcNow - since < OfflineRetry) return FileState.Offline;
        FileState state;
        if (!DirThere(dir)) state = FileState.Offline;
        else
        {
            // Not File.Exists: it says false for any error (a dropped SMB session, a timeout, access denied while the share
            // re-authenticates), and Missing drops the entry from the index. Only the server's "no such file" is a delete.
            try { File.GetAttributes(path); state = FileState.Present; }
            catch (FileNotFoundException) { state = DirThere(dir) ? FileState.Missing : FileState.Offline; }
            catch { state = FileState.Offline; }
        }
        if (state == FileState.Offline) _offlineDirs[dir] = DateTime.UtcNow; else _offlineDirs.TryRemove(dir, out _);
        return state;
    }

    /// <summary>
    /// Puts a file check on its row (UI thread). False when the result no longer applies (the entry moved on meanwhile: archived,
    /// or dropped from the index here because the archive answered without the file). <paramref name="fromAction"/>: the user
    /// clicked something, so the reason replaces whatever the status line said; the background check leaves an upload line alone.
    /// </summary>
    private bool Apply(ClipRow row, string path, bool archived, FileState state, bool fromAction)
    {
        var e = row.Entry;
        if (!string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) || e.IsArchived != archived) return false;
        bool wasGone = row.DeletedFromArchive;
        row.HasLocalFile = state == FileState.Present;
        row.DeletedFromArchive = archived && state == FileState.Missing;
        if (state == FileState.Present) row.ClearFileProblem();
        else if (archived)   // a deleted local clip says so in its Cloud meta line
        {
            string why = state == FileState.Offline ? ClipRow.ArchiveOffline : ClipRow.DeletedFromArchiveText;
            if (fromAction) row.SetProblem(why); else row.SetFileProblem(why);
        }
        if (row.DeletedFromArchive && !e.IsShared && ReferenceEquals(App.Library.Find(path), e))
        {
            // The archive answered and the file is not in it (deleted on the NAS): drop it from the index, the rule Load applies to
            // a deleted local file. A shared one stays: the Shared tab lists it by its link.
            Log.Info($"clips: {path} is no longer in the archive, removed from the library");
            App.Library.Remove(e);
            return false;
        }
        if (wasGone != row.DeletedFromArchive) Dispatcher.BeginInvoke(() => Refresh());   // leaves (or rejoins) the Local tab
        return true;
    }

    /// <summary>
    /// Is the clip's file there right now (before Play / folder / Trim / Share)? An archived file is looked up on a worker, with a
    /// "Looking in the archive…" line when that takes a moment, so an offline NAS never freezes the window. False = the row says why.
    /// </summary>
    private async Task<bool> EnsureFileAsync(ClipRow row)
    {
        var e = row.Entry;
        string path = e.Path;
        bool archived = e.IsArchived;
        bool wasThere = row.HasLocalFile;
        FileState state;
        if (!archived) state = e.Exists ? FileState.Present : FileState.Missing;
        else
        {
            if (row.Looking) return false;   // a second click while the first lookup is still waiting on the share
            if (!row.HasLocalFile && !row.DeletedFromArchive)
            {
                // Already found offline: answer at once instead of waiting out the SMB timeout (~20 s) for the same answer. It is
                // looked at again in the background and the row clears itself if the share is back (the click is not replayed).
                _ = RecheckArchivedAsync(row);
                row.SetProblem(ClipRow.ArchiveOffline);
                return false;
            }
            row.Looking = true;
            try
            {
                var probe = Task.Run(() => Probe(path, true, useOfflineCache: false));
                if (await Task.WhenAny(probe, Task.Delay(250)) != probe) { row.IsProblem = false; row.Status = ClipRow.LookingText; }
                state = await probe;
            }
            finally { row.Looking = false; }
            if (row.Status == ClipRow.LookingText) row.Status = null;
        }
        if (!Apply(row, path, archived, state, fromAction: true))
        {
            if (archived && state == FileState.Missing && !e.IsShared) App.Say(e.FileName + " is no longer in the archive");   // dropped from the list
            return false;
        }
        if (state == FileState.Present)
        {
            if (!wasThere && row.Thumbnail == null) { row.Checked = false; Refresh(); }   // back after being offline: make its thumbnail now
            return true;
        }
        if (!archived)
        {
            row.SetProblem(ClipRow.LocalDeleted);
            App.Say(e.FileName + " is gone (deleted or moved outside Clink)");
            Refresh();   // leaves Local / Trimmed (a shared clip stays in Shared, marked "local file deleted")
        }
        return false;
    }

    /// <summary>One fresh look at an offline archived row's file on a worker; the row follows the answer (and gets its thumbnail when back).</summary>
    private async Task RecheckArchivedAsync(ClipRow row)
    {
        string path = row.Entry.Path;
        row.Looking = true;
        FileState state;
        try { state = await Task.Run(() => Probe(path, true, useOfflineCache: false)); }
        finally { row.Looking = false; }
        if (Apply(row, path, true, state, fromAction: false) && state == FileState.Present && row.Thumbnail == null) { row.Checked = false; Refresh(); }
    }

    /// <summary>After a refused click on an archived row: the plain "archive offline" says what could not be done.</summary>
    private static void SayCannot(ClipRow row, string what)
    {
        if (row.Status == ClipRow.ArchiveOffline) row.SetProblem($"{ClipRow.ArchiveOffline}: cannot {what} right now");
    }

    // ------------------------------------------------------------------ actions

    private static ClipRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipRow;

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row != null) await ShareAsync(row);
    }

    internal async Task ShareAsync(ClipRow row)
    {
        if (row.IsUploading) return;
        row.Status = null;
        row.IsProblem = false;   // a retry after a failure is progress again, not amber
        row.Progress = 0;
        if (!row.HasLink)
        {
            row.IsUploading = true;   // also keeps a second click out while an archived file is looked up
            if (!await EnsureFileAsync(row)) { row.IsUploading = false; return; }
        }
        row.Status = row.HasLink ? null : "Uploading…";
        var progress = new Progress<double>(p => { row.Progress = p; row.Status = $"Uploading… {p:P0}"; });
        bool ok = await App.ShareAsync(row.Entry, progress);
        row.IsUploading = false;
        var (status, problem) = ShareStatusFor(ok, App.LastShareOutcome, App.ShareRefusal, App.ShareError, App.Settings.CloudSharingEnabled);
        row.Status = status;
        row.IsProblem = problem;
        if (status == null) return;   // the share window opened (no token): nothing failed, no status line
        row.Refresh();
        if (ok)
        {
            Refresh();   // the entry is now shared: it joins the Shared tab and gets the cloud icon
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (row.Status?.StartsWith("Link") == true) row.Status = null; }));
        }
    }

    /// <summary>
    /// The row's status line (null = none) and whether it is a problem (amber) after a share. Not shared on purpose is neutral: the
    /// share window opened (no token), or the user said "Not now" / cloud sharing was switched off meanwhile. Copium's limits, a
    /// failed upload and a share window that could not open are problems. Pure.
    /// </summary>
    internal static (string? Status, bool Problem) ShareStatusFor(bool ok, ShareOutcome outcome, string? refusal, string? error, bool cloudOn)
    {
        if (ok) return ("Link copied to clipboard", false);
        if (outcome == ShareOutcome.FileShareOpened) return (null, false);
        if (outcome == ShareOutcome.Declined) return (cloudOn ? "Not shared" : "Not shared: cloud sharing is off", false);
        return (refusal ?? (outcome == ShareOutcome.WindowFailed ? AppController.ShareWindowFailedText
            : error is string why ? "Upload failed: " + why : "Upload failed"), true);
    }

    // Trim / folder / Play look for the file first (EnsureFileAsync): an archived clip on an offline NAS says "archive offline"
    // instead of freezing the window for the SMB timeout, and the folder button never opens some other folder.

    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null) return;
        if (!await EnsureFileAsync(row)) { SayCannot(row, "trim"); return; }
        TrimRequested?.Invoke(row.Entry);
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null) return;
        if (!await EnsureFileAsync(row)) { SayCannot(row, "open its folder"); return; }
        try { Process.Start("explorer.exe", $"/select,\"{row.Entry.Path}\""); }
        catch (Exception ex) { App.Say("Could not open folder: " + ex.Message); }
    }

    private object? _pressed;   // the row (clip or screenshot) the left button went down on: a click that began elsewhere must not open this one

    private void Row_Down(object sender, MouseButtonEventArgs e) => _pressed = RowOf(sender);

    /// <summary>The click landed on one of the row's own buttons (Trim, Share, the folder, the link): the button handles it, the row does not.</summary>
    private static bool OnButton(MouseButtonEventArgs e, object row)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, row); d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    /// <summary>True when the button that came up now went down on this row too (and forgets it).</summary>
    private bool ClickedOn(object row)
    {
        var pressed = _pressed;
        _pressed = null;
        return ReferenceEquals(pressed, row);
    }

    /// <summary>A click on the row plays the clip, unless it was on one of the row's own buttons (Trim, Share, the folder, the link).</summary>
    private async void Row_Click(object sender, MouseButtonEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null || e.Handled || !ClickedOn(row)) return;
        if (OnButton(e, sender)) return;
        if (!row.HasLocalFile && !row.Entry.IsArchived) return;   // a shared clip whose file is gone has nothing to play
        e.Handled = true;
        await PlayAsync(row);
    }

    private async void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // the row around the thumbnail would play the clip a second time
        var row = RowOf(sender);
        if (row == null || !ClickedOn(row)) return;
        await PlayAsync(row);
    }

    /// <summary>Plays a clip: the window opens its player; with nobody listening the system's own player does.</summary>
    private async Task PlayAsync(ClipRow row)
    {
        if (!await EnsureFileAsync(row)) { SayCannot(row, "play it"); return; }
        if (PlayRequested is { } play) { play(row.Entry); return; }
        string path = row.Entry.Path;
        void Open()
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Dispatcher.BeginInvoke(() => App.Say("Open failed: " + ex.Message)); }
        }
        if (!row.Entry.IsArchived) { Open(); return; }
        // ShellExecute on a share path touches the share again: on its own STA thread (shell extensions expect one), not the UI's.
        var t = new Thread(Open) { IsBackground = true, Name = "Clink.OpenArchived" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row?.Link == null) return;
        App.CopyToClipboard(row.Link);
        row.Status = "Link copied to clipboard";
        row.IsProblem = false;
        _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (row.Status?.StartsWith("Link") == true) row.Status = null; }));
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        string? url = row?.Entry.CopiumPageUrl ?? row?.Link;
        if (url == null) return;
        // The address came from the server or from the clip list file: only an https address is handed to the shell.
        if (!Clink.Services.CopiumClient.IsHttpsUrl(url, out var page)) { Log.Warn("clips: not opened: the clip's link is not an https address"); return; }
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { App.Say("Could not open the browser: " + ex.Message); }
    }

    // ------------------------------------------------------------------ screenshots side

    private static ScreenshotRow? ShotOf(object sender) => (sender as FrameworkElement)?.DataContext as ScreenshotRow;

    /// <summary>Starts to listen to the screenshot history (once per service): saves, uploads and deletes refresh the list.</summary>
    private ScreenshotService HookShots()
    {
        var svc = App.Screenshots;
        if (!ReferenceEquals(_shotService, svc))
        {
            _shotService = svc;
            _shotModel = new ScreenshotListModel(() => svc.HistoryEntries, folders: svc.KnownFolders);
            svc.HistoryChanged += () => Dispatcher.BeginInvoke(OnShotHistoryChanged);   // raised on the saving / uploading thread
        }
        return svc;
    }

    private void OnShotHistoryChanged()
    {
        if (Feature == LibraryFeature.Screenshots && IsVisible) RefreshShots();
    }

    /// <summary>The history and the files are read on a worker; the rows are put together here when it answers (a newer refresh wins).</summary>
    private async void RefreshShots()
    {
        if (!App.Settings.ScreenshotsEnabled) return;
        int gen = ++_shotGen;
        ScreenshotListModel model;
        try { HookShots(); model = _shotModel!; }
        catch (Exception ex) { Log.Warn("library: screenshots: " + ex.Message); return; }
        // The right list, labels and empty text at once, from what was read last (the clips list is never left on screen while this loads).
        if (_shotsLoaded) ApplyShots(); else { SetLabels(0, 0); ShowList(LibraryFeature.Screenshots, 1); }
        List<ScreenshotItem> items;
        try { items = await model.LoadAsync(); }
        catch (Exception ex)
        {
            Log.Warn("library: screenshot history: " + ex.Message);
            items = new();   // the empty text says there is nothing to show
        }
        if (gen != _shotGen || Feature != LibraryFeature.Screenshots) return;
        _shotItems = items;
        _shotsLoaded = true;
        ApplyShots();
    }

    private void ApplyShots()
    {
        var items = _shotItems;
        var (local, shared) = LibraryView.Counts(items, false, ScreenshotListModel.IsLocal, ScreenshotListModel.IsShared, _ => false);
        var rows = new List<ScreenshotRow>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!keep.Add(item.Entry.Path)) continue;
            if (_shotRows.TryGetValue(item.Entry.Path, out var row)) row.Update(item); else _shotRows[item.Entry.Path] = row = new ScreenshotRow(item);
        }
        foreach (var gone in _shotRows.Keys.Where(k => !keep.Contains(k)).ToList()) _shotRows.Remove(gone);
        foreach (var item in ScreenshotListModel.Pick(items, _level)) rows.Add(_shotRows[item.Entry.Path]);
        Replace(_shots, rows);
        SetLabels(local, shared);
        ShowList(LibraryFeature.Screenshots, rows.Count);
        // Only rows that have a visual get a thumbnail (a long history is not decoded in one go): a row that is realized later asks itself
        // (ShotRow_Loaded / ShotRow_DataContextChanged, as the list scrolls); the ones already on screen are asked now.
        if (IsVisible) QueueThumbnails(rows.Where(r => ShotList.ItemContainerGenerator.ContainerFromItem(r) != null));
    }

    private void ShotRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (ShotOf(sender) is { } row && IsVisible && Feature == LibraryFeature.Screenshots) QueueThumbnails(new[] { row });
    }

    private void ShotRow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ScreenshotRow row && IsVisible && Feature == LibraryFeature.Screenshots) QueueThumbnails(new[] { row });
    }

    /// <summary>One at a time on workers (the model decodes off the UI thread); a row is asked once, and a pane that is hidden asks no more.</summary>
    private void QueueThumbnails(IEnumerable<ScreenshotRow> rows)
    {
        foreach (var r in rows)
        {
            if (!r.HasLocalFile || r.Thumbnail != null || r.ThumbnailRequested) continue;
            r.ThumbnailRequested = true;
            _thumbQueue.Enqueue(r);
        }
        if (!_thumbLoop && _thumbQueue.Count > 0) _ = ThumbnailLoopAsync();
    }

    private async Task ThumbnailLoopAsync()
    {
        _thumbLoop = true;
        try
        {
            while (_thumbQueue.Count > 0)
            {
                var row = _thumbQueue.Dequeue();
                var model = _shotModel;
                // asked again when the pane is shown on the screenshots side (also: the clips side was switched to meanwhile)
                if (model == null || !IsVisible || Feature != LibraryFeature.Screenshots) { row.ThumbnailRequested = false; continue; }
                BitmapSource? bmp = null;
                int version = row.ThumbnailVersion;
                try { bmp = await model.ThumbnailAsync(row.Entry.Path); } catch { }
                row.TrySetThumbnail(bmp, version);   // not when the row's file changed while it decoded
            }
        }
        finally { _thumbLoop = false; }
    }

    private void Shot_Down(object sender, MouseButtonEventArgs e) => _pressed = ShotOf(sender);

    /// <summary>A click on the row (not on one of its buttons) opens the editor.</summary>
    private void Shot_Click(object sender, MouseButtonEventArgs e)
    {
        var row = ShotOf(sender);
        if (row == null || e.Handled || !ClickedOn(row) || OnButton(e, sender) || !row.HasLocalFile) return;
        e.Handled = true;
        EditShot(row);
    }

    private void ShotEdit_Click(object sender, RoutedEventArgs e)
    {
        if (ShotOf(sender) is { } row) EditShot(row);
    }

    /// <summary>The service checks the file (a screenshot of this app, a readable .png), opens the editor and reports a failure itself.</summary>
    private void EditShot(ScreenshotRow row) => App.Screenshots.EditFile(row.Entry.Path);

    private void ShotOpen_Click(object sender, RoutedEventArgs e)
    {
        var row = ShotOf(sender);
        if (row == null) return;
        string path = row.Entry.Path;
        // Shell-executed only as a .png in a screenshots folder (never a path that only the history file names).
        if (!App.Screenshots.IsOpenableEntry(path) || !File.Exists(path))
        {
            Log.Warn("library: screenshot not opened: not a .png in a screenshots folder");
            App.Say("Could not open the screenshot: it is not in the screenshots folder, or it is gone");
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { App.Say("Could not open the screenshot: " + ex.Message); }
    }

    private void ShotFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = ShotOf(sender);
        if (row == null) return;
        // The same rule as Open / Edit: a path only the history file names is never probed or handed to the shell.
        if (!App.Screenshots.IsOpenableEntry(row.Entry.Path)) { App.Say("The screenshot file is gone, or it is not in the screenshots folder"); return; }
        try { Process.Start("explorer.exe", $"/select,\"{row.Entry.Path}\""); }
        catch (Exception ex) { App.Say("Could not open folder: " + ex.Message); }
    }

    /// <summary>Upload goes through the service, which asks for the Copium consent first (and refuses when cloud sharing is off).</summary>
    private async void ShotUpload_Click(object sender, RoutedEventArgs e)
    {
        var row = ShotOf(sender);
        if (row == null || row.IsUploading) return;
        row.IsProblem = false;
        row.Status = "Uploading…";
        row.IsUploading = true;
        bool ok = false;
        try { ok = await App.Screenshots.UploadAsync(row.Entry, manual: true, askAs: ConsentContext.ScreenshotUploadLibrary); }
        catch (Exception ex) { Log.Error("library: screenshot upload", ex); }
        finally { row.IsUploading = false; }
        row.Refresh();
        if (ok)
        {
            row.Status = "Uploaded";
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (row.Status == "Uploaded") row.Status = null; }));
        }
        else row.SetProblem(App.Settings.CloudSharingEnabled ? "Not uploaded" : "Not uploaded: cloud sharing is off");   // the reason was told in a toast
    }

    private void ShotCopyLink_Click(object sender, RoutedEventArgs e)
    {
        var row = ShotOf(sender);
        if (row?.Link == null) return;
        App.CopyToClipboard(row.Link);
        row.Status = "Link copied to clipboard";
        row.IsProblem = false;
        _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (row.Status?.StartsWith("Link") == true) row.Status = null; }));
    }

    private void ShotOpenPage_Click(object sender, RoutedEventArgs e)
    {
        var row = ShotOf(sender);
        string? url = row?.Entry.CopiumPageUrl ?? row?.Link;
        if (url == null) return;
        if (!CopiumClient.IsHttpsUrl(url, out var page)) { Log.Warn("library: screenshot page not opened: the link is not an https address"); return; }
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { App.Say("Could not open the browser: " + ex.Message); }
    }

    /// <summary>
    /// Asks first (naming the file); the picture goes to the Recycle Bin (it can be restored) and leaves the list, except a shared one,
    /// which stays with its link (Copy link / Open page / Remove from list). The link on Copium is never touched.
    /// </summary>
    private async void ShotDelete_Click(object sender, RoutedEventArgs e)
    {
        var row = ShotOf(sender);
        if (row == null || row.IsDeleting || row.IsUploading) return;
        string what = ScreenshotRow.ConfirmText(row.Entry, row.HasLocalFile);
        var owner = Window.GetWindow(this);
        var answer = owner != null
            ? MessageBox.Show(owner, what, "Clink", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(what, "Clink", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        row.IsDeleting = true;
        var result = ScreenshotDeleteResult.Failed;
        nint ownerHandle = 0;
        try
        {
            var w = owner ?? Application.Current?.MainWindow;
            if (w != null) ownerHandle = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        }
        catch { }
        try { result = await App.Screenshots.DeleteAsync(row.Entry, ownerHandle); }   // IsDeleting stays set until the shell call returns
        catch (Exception ex) { Log.Warn("library: screenshot delete: " + ex.Message); }
        finally { row.IsDeleting = false; }
        if (result is ScreenshotDeleteResult.Deleted or ScreenshotDeleteResult.ListNotUpdated) RefreshShots();   // (the history says so too, once the entry was in it)
        if (result != ScreenshotDeleteResult.Deleted)
            row.SetProblem(ScreenshotRow.DeleteProblemText(result, ScreenshotService.IsOnLocalFixedDrive(row.Entry.Path)));
    }
}

/// <summary>One library entry as a row in the clips lists (shared by the Local and Shared levels, trimmed or not).</summary>
internal sealed class ClipRow : INotifyPropertyChanged
{
    // Status lines about the file itself (cleared once the file is there again; an upload status is left alone).
    public const string ArchiveOffline = "archive offline", DeletedFromArchiveText = "deleted from the archive", LocalDeleted = "local file deleted";
    public const string LookingText = "Looking in the archive…";

    public ClipEntry Entry { get; }
    private BitmapSource? _thumb;
    private bool _uploading, _problem, _hasLocal = true, _gone;
    private double _progress;
    private string? _status;

    public ClipRow(ClipEntry e) { Entry = e; }

    public string Title => string.IsNullOrEmpty(Entry.Game) ? (Entry.IsTrim ? "Trimmed clip" : "Clip") : Entry.Game! + (Entry.IsTrim ? " (trim)" : "");
    public string Meta
    {
        get
        {
            string dur = Entry.Duration > 0 ? Capabilities.FormatClipLength(Entry.Duration) + " · " : "";
            // Decimal MB everywhere a size is shown: the unit of the Copium / Discord limits and of the Share pre-check.
            return $"{Entry.Created:MMM d, HH:mm} · {dur}{Capabilities.FormatBytes(Entry.Size)}";
        }
    }
    /// <summary>Shared tab: date, size and where the local copy is (or that it is gone).</summary>
    public string CloudMeta
    {
        get
        {
            string size = Entry.Size > 0 ? $" · {Capabilities.FormatBytes(Entry.Size)}" : "";
            string where = HasLocalFile ? (Entry.IsArchived ? " · in the archive" : "")
                : !Entry.IsArchived ? " · " + LocalDeleted : DeletedFromArchive ? " · " + DeletedFromArchiveText : " · " + ArchiveOffline;
            return $"{Entry.Created:MMM d, yyyy HH:mm}{size}{where}";
        }
    }
    public string ShareLabel => HasLink ? "Copy link" : "Share";
    /// <summary>The Share button shows: always for a clip that has a link (Copy link needs no network), otherwise only while cloud sharing is on (Settings → App).</summary>
    public bool ShareVisible => HasLink || AppController.Instance?.Settings.CloudSharingEnabled != false;
    public bool HasLink => !string.IsNullOrEmpty(Entry.CopiumLink);
    public string? Link => Entry.CopiumLink;
    public bool IsShared => Entry.IsShared;
    public bool IsTrim => Entry.IsTrim;
    public bool IsArchived => Entry.IsArchived;
    public bool IsLocalOnly => !Entry.IsShared && !Entry.IsTrim && !Entry.IsArchived;
    public string ArchiveTip => Entry.IsArchived ? $"Archived {Entry.ArchivedAt:MMM d, HH:mm} → {System.IO.Path.GetDirectoryName(Entry.Path)}" : "";

    /// <summary>The file is there (checked on a worker; true until known so rows do not flicker).</summary>
    public bool HasLocalFile { get => _hasLocal; set { if (_hasLocal == value) return; _hasLocal = value; Raise(); Raise(nameof(CloudMeta)); } }
    /// <summary>Archived, the archive answered, and the file is not in it (deleted on the NAS): not listed under Local any more.</summary>
    public bool DeletedFromArchive { get => _gone; set { if (_gone == value) return; _gone = value; Raise(nameof(CloudMeta)); } }
    /// <summary>The worker has looked at the file (archived rows that were not there are looked at again on every refresh).</summary>
    public bool Checked { get; set; }
    /// <summary><see cref="ClipEntry.Size"/> when the worker looked: a different size later means a new file under the same entry.</summary>
    public long CheckedSize { get; set; }
    /// <summary>An action is waiting on the archive for this file (<see cref="ClipsPane"/> EnsureFileAsync).</summary>
    public bool Looking { get; set; }
    public BitmapSource? Thumbnail { get => _thumb; set { _thumb = value; Raise(); } }
    public bool IsUploading { get => _uploading; set { _uploading = value; Raise(); } }
    public double Progress { get => _progress; set { _progress = value; Raise(); } }
    public string? Status { get => _status; set { _status = value; Raise(); } }
    /// <summary>The status line is a problem (amber) rather than progress (accent).</summary>
    public bool IsProblem { get => _problem; set { _problem = value; Raise(); } }

    // Where the row sits in the Local list (set by the pane from ClipGrouping.Flatten; Plain everywhere else: Trimmed filter, Shared level).
    private ClipRowKind _kind;
    private int _childCount;
    private bool _expanded;
    public bool IsParent => _kind == ClipRowKind.Parent;
    public bool IsChild => _kind == ClipRowKind.Child;
    public bool IsExpanded => _expanded;
    public int ChildCount => _childCount;
    public string TrimsLabel => ClipGrouping.TrimsLabel(_childCount);
    public string ToggleName => ClipGrouping.ToggleName(_expanded, _childCount, Title);
    /// <summary>The row's outer margin: a trim under its clip is indented.</summary>
    public Thickness NestMargin => new(IsChild ? 28 : 0, 0, 0, 2);

    public void SetNesting(ClipRowKind kind, int childCount, bool expanded)
    {
        if (_kind == kind && _childCount == childCount && _expanded == expanded) return;
        _kind = kind; _childCount = childCount; _expanded = expanded;
        foreach (var p in new[] { nameof(IsParent), nameof(IsChild), nameof(IsExpanded), nameof(ChildCount), nameof(TrimsLabel), nameof(ToggleName), nameof(NestMargin) })
            Raise(p);
    }

    public void SetProblem(string text) { IsProblem = true; Status = text; }

    /// <summary>
    /// A file problem from the background check: shown unless the line already says something else (an upload, a copied link)
    /// or says the same more precisely ("archive offline: cannot trim right now" after a click).
    /// </summary>
    public void SetFileProblem(string text)
    {
        if (_status == null || (IsFileProblem(_status) && !_status.StartsWith(text, StringComparison.Ordinal))) SetProblem(text);
    }

    /// <summary>The file is there (again): drop an "archive offline" / "deleted" line, keep anything else.</summary>
    public void ClearFileProblem()
    {
        if (_problem && _status is string s && IsFileProblem(s))
        {
            Status = null;
            IsProblem = false;
        }
    }

    private static bool IsFileProblem(string s) => s.StartsWith(ArchiveOffline, StringComparison.Ordinal) || s == DeletedFromArchiveText || s == LocalDeleted;

    public void Refresh()
    {
        foreach (var p in new[] { nameof(ShareLabel), nameof(ShareVisible), nameof(HasLink), nameof(Link), nameof(Meta), nameof(CloudMeta), nameof(Title), nameof(IsShared), nameof(IsTrim), nameof(IsArchived), nameof(IsLocalOnly), nameof(ArchiveTip), nameof(ToggleName) })
            Raise(p);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
