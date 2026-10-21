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
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The clips pane (docs/SPEC-v2.md §1): Local (every local file, with shared / trim / local-only / archived icons), Cloud
/// (everything shared, listed by link even after the local file is gone) and Trimmed (local files made by the trim editor).
/// Rows come from <see cref="AppController.Library"/>; thumbnails and file checks run on a worker so an offline archive never
/// stalls the window. Share works here; Trim asks the window (<see cref="TrimRequested"/>) so the editor gets the right owner.
/// </summary>
public partial class ClipsPane : UserControl
{
    /// <summary>An archive folder that did not answer is not asked again for this long (each try can block for the SMB timeout).</summary>
    private static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(30);

    private readonly ObservableCollection<ClipRow> _local = new(), _cloud = new(), _trim = new();
    private readonly Dictionary<ClipEntry, ClipRow> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, DateTime> _offlineDirs = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshGen;

    public ClipsPane()
    {
        InitializeComponent();
        LocalList.ItemsSource = _local;
        CloudList.ItemsSource = _cloud;
        TrimList.ItemsSource = _trim;
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

    private static AppController App => AppController.Instance;

    /// <summary>Re-reads the library. Rows for entries already shown keep their thumbnail and upload state.</summary>
    public void Refresh()
    {
        if (App == null) return;
        var entries = App.Library.Entries;
        var keep = new HashSet<ClipEntry>(ReferenceEqualityComparer.Instance);
        var local = new List<ClipRow>();
        var cloud = new List<ClipRow>();
        var trim = new List<ClipRow>();
        foreach (var e in entries)
        {
            // Archived entries are not File.Exists-checked on the UI thread (an offline share can block); the worker says "archive offline".
            bool exists = !e.IsArchived && e.Exists;
            if (!e.IsShared && !e.IsArchived && !exists) continue;   // the library drops these on load; a late delete shows up here
            var row = RowFor(e);
            keep.Add(e);
            if (!e.IsArchived) row.HasLocalFile = exists;   // a shared clip deleted outside Clink: no Play / folder, "local file deleted"
            else if (row.Checked && !row.HasLocalFile) row.Checked = false;   // look again: the archive may be back (or the file deleted there)
            bool hasFile = e.IsArchived ? !row.DeletedFromArchive : exists;
            if (hasFile) local.Add(row);
            if (hasFile && e.IsTrim) trim.Add(row);
            if (e.IsShared) cloud.Add(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k)).ToList()) _rows.Remove(gone);
        Replace(_local, local);
        Replace(_cloud, cloud);
        Replace(_trim, trim);
        NoLocalText.Visibility = _local.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCloudText.Visibility = _cloud.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTrimText.Visibility = _trim.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalTab.Header = _local.Count > 0 ? $"Local ({_local.Count})" : "Local";
        CloudTab.Header = _cloud.Count > 0 ? $"Cloud ({_cloud.Count})" : "Cloud";
        TrimTab.Header = _trim.Count > 0 ? $"Trimmed ({_trim.Count})" : "Trimmed";
        // Only listed rows, in list order (newest first), and only while the pane is on screen (IsVisibleChanged refreshes on show).
        if (IsVisible) LoadThumbnails(_local.Concat(_cloud).Concat(_trim).Distinct().Where(r => !r.Checked).ToList());
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

    private static void Replace(ObservableCollection<ClipRow> target, IReadOnlyList<ClipRow> rows)
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
    /// only when the folder still answers after the file check, so a NAS dropping in between never reads as a delete). A folder that
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
        FileState state = !DirThere(dir) ? FileState.Offline : FileThere(path) ? FileState.Present : DirThere(dir) ? FileState.Missing : FileState.Offline;
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
            // a deleted local file. A shared one stays: the Cloud tab lists it by its link.
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
            Refresh();   // leaves Local / Trimmed (a shared clip stays in Cloud, marked "local file deleted")
        }
        return false;
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
        // Refused by Copium's free limits, no token yet (nothing was tried), or really failed (the footer has the detail).
        row.Status = ok ? "Link copied to clipboard"
            : App.ShareRefusal ?? (string.IsNullOrWhiteSpace(App.Settings.CopiumToken) ? "Add your Copium token first (Clip tab)" : "Upload failed");
        row.IsProblem = !ok;
        row.Refresh();
        if (ok)
        {
            Refresh();   // the entry is now shared: it joins the Cloud tab and gets the cloud icon
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (row.Status?.StartsWith("Link") == true) row.Status = null; }));
        }
    }

    // Trim / folder / Play look for the file first (EnsureFileAsync): an archived clip on an offline NAS says "archive offline"
    // instead of freezing the window for the SMB timeout, and the folder button never opens some other folder.

    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null) return;
        if (!await EnsureFileAsync(row))
        {
            if (row.Status == ClipRow.ArchiveOffline) row.SetProblem(ClipRow.ArchiveOffline + ": cannot trim right now");
            return;
        }
        TrimRequested?.Invoke(row.Entry);
    }

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null || !await EnsureFileAsync(row)) return;
        try { Process.Start("explorer.exe", $"/select,\"{row.Entry.Path}\""); }
        catch (Exception ex) { App.Say("Could not open folder: " + ex.Message); }
    }

    private async void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null || !await EnsureFileAsync(row)) return;
        try { Process.Start(new ProcessStartInfo(row.Entry.Path) { UseShellExecute = true }); }
        catch (Exception ex) { App.Say("Open failed: " + ex.Message); }
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
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Say("Could not open the browser: " + ex.Message); }
    }
}

/// <summary>One library entry as a row in the clips lists (shared by the Local, Cloud and Trimmed tabs).</summary>
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
            // h:mm:ss from an hour up: a full 60-minute replay (3600 s plus up to one GOP) is not "0:00"
            string dur = Entry.Duration > 0 ? TimeSpan.FromSeconds(Entry.Duration).ToString(Entry.Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss") + " · " : "";
            // Decimal MB everywhere a size is shown: the unit of the Copium / Discord limits and of the Share pre-check.
            return $"{Entry.Created:MMM d, HH:mm} · {dur}{Capabilities.FormatBytes(Entry.Size)}";
        }
    }
    /// <summary>Cloud tab: date, size and where the local copy is (or that it is gone).</summary>
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

    public void SetProblem(string text) { IsProblem = true; Status = text; }

    /// <summary>A file problem from the background check: shown unless the line already says something else (an upload, a copied link).</summary>
    public void SetFileProblem(string text) { if (_status == null || IsFileProblem(_status)) SetProblem(text); }

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
        foreach (var p in new[] { nameof(ShareLabel), nameof(HasLink), nameof(Link), nameof(Meta), nameof(CloudMeta), nameof(Title), nameof(IsShared), nameof(IsTrim), nameof(IsArchived), nameof(IsLocalOnly), nameof(ArchiveTip) })
            Raise(p);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
