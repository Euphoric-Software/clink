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
    private const int MaxLocalRows = 60;

    private readonly ObservableCollection<ClipRow> _local = new(), _cloud = new(), _trim = new();
    private readonly Dictionary<ClipEntry, ClipRow> _rows = new(ReferenceEqualityComparer.Instance);
    private int _refreshGen;

    public ClipsPane()
    {
        InitializeComponent();
        LocalList.ItemsSource = _local;
        CloudList.ItemsSource = _cloud;
        TrimList.ItemsSource = _trim;
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
            if (!e.IsShared && !e.IsArchived && !e.Exists) continue;   // the library drops these on load; a late delete shows up here
            var row = RowFor(e);
            keep.Add(e);
            // Archived entries are not File.Exists-checked on the UI thread (an offline share can block); the worker says "archive offline".
            bool hasFile = e.IsArchived || e.Exists;
            if (hasFile && local.Count < MaxLocalRows) local.Add(row);
            if (hasFile && e.IsTrim) trim.Add(row);   // every trim, not only those among the newest rows of the Local tab
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
        LoadThumbnails(_rows.Values.Where(r => r.Thumbnail == null && !r.Checked).ToList());
    }

    private ClipRow RowFor(ClipEntry e)
    {
        if (!_rows.TryGetValue(e, out var row)) { row = new ClipRow(e); _rows[e] = row; }
        else row.Refresh();
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
                bool exists;
                try { exists = File.Exists(path); } catch { exists = false; }
                BitmapSource? bmp = null;
                if (exists) { try { bmp = Thumbnails.Get(path, 176, 100); } catch { } }
                Dispatcher.BeginInvoke(() =>
                {
                    row.Checked = true;
                    row.HasLocalFile = exists;
                    if (bmp != null) row.Thumbnail = bmp;
                    if (!exists && row.Entry.IsArchived) row.SetProblem("archive offline");   // a deleted shared clip says so in its Cloud meta line
                });
            }
        });
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
        row.IsUploading = !row.HasLink;
        row.Status = row.HasLink ? null : "Uploading…";
        var progress = new Progress<double>(p => { row.Progress = p; row.Status = $"Uploading… {p:P0}"; });
        bool ok = await App.ShareAsync(row.Entry, progress);
        row.IsUploading = false;
        row.Status = ok ? "Link copied to clipboard" : App.ShareRefusal ?? "Upload failed";   // refused by Copium's free limits, or really failed (the footer has the detail)
        row.IsProblem = !ok;
        row.Refresh();
        if (ok)
        {
            Refresh();   // the entry is now shared: it joins the Cloud tab and gets the cloud icon
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (row.Status?.StartsWith("Link") == true) row.Status = null; }));
        }
    }

    private void Trim_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null) return;
        if (!row.HasLocalFile) { row.SetProblem(row.Entry.IsArchived ? "archive offline: cannot trim right now" : "local file deleted"); return; }
        TrimRequested?.Invoke(row.Entry);
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var row = RowOf(sender);
        if (row != null) App.OpenFolder(row.Entry.Path);
    }

    private void Thumb_Click(object sender, MouseButtonEventArgs e)
    {
        var row = RowOf(sender);
        if (row == null) return;
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
    public ClipEntry Entry { get; }
    private BitmapSource? _thumb;
    private bool _uploading, _problem, _hasLocal = true;
    private double _progress;
    private string? _status;

    public ClipRow(ClipEntry e) { Entry = e; }

    public string Title => string.IsNullOrEmpty(Entry.Game) ? (Entry.IsTrim ? "Trimmed clip" : "Clip") : Entry.Game! + (Entry.IsTrim ? " (trim)" : "");
    public string Meta
    {
        get
        {
            string dur = Entry.Duration > 0 ? $"{TimeSpan.FromSeconds(Entry.Duration):m\\:ss} · " : "";
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
            string where = !HasLocalFile ? " · local file deleted" : Entry.IsArchived ? " · in the archive" : "";
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
    /// <summary>The worker has looked at the file once.</summary>
    public bool Checked { get; set; }
    public BitmapSource? Thumbnail { get => _thumb; set { _thumb = value; Raise(); } }
    public bool IsUploading { get => _uploading; set { _uploading = value; Raise(); } }
    public double Progress { get => _progress; set { _progress = value; Raise(); } }
    public string? Status { get => _status; set { _status = value; Raise(); } }
    /// <summary>The status line is a problem (amber) rather than progress (accent).</summary>
    public bool IsProblem { get => _problem; set { _problem = value; Raise(); } }

    public void SetProblem(string text) { IsProblem = true; Status = text; }

    public void Refresh()
    {
        foreach (var p in new[] { nameof(ShareLabel), nameof(HasLink), nameof(Link), nameof(Meta), nameof(CloudMeta), nameof(Title), nameof(IsShared), nameof(IsTrim), nameof(IsArchived), nameof(IsLocalOnly), nameof(ArchiveTip) })
            Raise(p);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
