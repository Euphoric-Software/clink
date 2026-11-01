using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clink.Native;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The share window for several files at once (the library's selection bar: Share files…): one card for all of them, to drag out
/// together into Discord, a browser upload box or a folder, or to copy (Ctrl+V pastes them all). Like <see cref="ShareWindow"/> it
/// only ever offers a <b>copy</b> (<see cref="ShareWindow.FileData(IReadOnlyList{string})"/>), floats on top except over a game, has no
/// owner, stays out of screen captures and shows no file path. Every file is looked at on a worker when it opens and whenever it comes
/// forward again; files that cannot be reached are left out of the drag and named in a warning line (the card is off when none is
/// there). Open folder works only when the files are all in one folder. One such window at a time (<see cref="AppController.OpenShareFilesWindow"/>).
/// </summary>
public partial class ShareFilesWindow : Window, ICaptureExcludedWindow
{
    private const int ToastMargin = 16;
    private const int DiscordLimit = 10;

    private readonly IReadOnlyList<ShareFileItem> _items;
    private readonly bool _screenshots;
    private List<string> _present;
    private bool _checking, _closed, _dragArmed, _openingFolder, _looking;
    private string? _thumbFor;   // the file the card's thumbnail is of
    private Point _dragFrom;

    static ShareFilesWindow() => CaptureExclusion.Register();

    internal ShareFilesWindow(IReadOnlyList<ShareFileItem> items, bool screenshots)
    {
        _items = items;
        _screenshots = screenshots;
        InitializeComponent();
        AppIcons.Attach(this);
        Title = TitleText.Text = TitleFor(items.Count, screenshots);
        bool nothingToSetUp = AppController.Instance?.Settings.CloudSharingEnabled == false || AppController.Instance is { HasCopiumToken: true };
        if (nothingToSetUp) SetUpCopiumButton.Visibility = Visibility.Collapsed;
        IntroText.Text = nothingToSetUp ? "Drag the files where you want them, or copy them:" : "No Copium token yet, so there is no link to copy. Share the files themselves:";
        // A local file is taken to be there until the worker says otherwise; an archived one waits for its share to answer.
        _present = items.Where(i => !i.IsArchived).Select(i => i.Path).ToList();
        _looking = items.Any(i => i.IsArchived);
        ShowState();
        Loaded += (_, _) => _ = CheckAsync();
        Activated += (_, _) => { if (!QuietMode.Active) Topmost = true; _ = CheckAsync(); };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateTopmost));
        SourceInitialized += (_, _) =>
        {
            ScreenPlacement.ExcludeFromCaptureUnlessDocShots(new WindowInteropHelper(this).Handle);
            AppController.Instance!.StateChanged += OnStateTick;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            AppController.Instance!.StateChanged -= OnStateTick;
        };
    }

    /// <summary>"Share 6 clips" / "Share 6 screenshots".</summary>
    internal static string TitleFor(int count, bool screenshots) => BulkActions.ShareWindowTitle(count, screenshots);

    /// <summary>The paths the drag and the clipboard carry right now (the files that were found).</summary>
    internal IReadOnlyList<string> PresentPaths => _present;

    // ------------------------------------------------------------------ the files behind the card

    private async Task CheckAsync()
    {
        if (_checking || _closed) return;
        _checking = true;
        try
        {
            var items = _items;
            if (_present.Count == 0) { _looking = true; ShowState(); }
            var states = await Task.Run(() => items.Select(i => ShareWindow.Probe(i.Path, i.IsArchived)).ToArray());
            if (_closed) return;
            _looking = false;
            _present = items.Where((_, k) => states[k] == ShareWindow.FileState.Present).Select(i => i.Path).ToList();
            ShowState();
            string? first = _present.Count > 0 ? _present[0] : null;
            if (first != null && _thumbFor != first)
            {
                BitmapSource? thumb = await Task.Run(() => Thumbnails.Get(first, 176, 100));
                if (!_closed && thumb != null && _present.Count > 0 && _present[0] == first) { ThumbImage.Source = thumb; _thumbFor = first; }
            }
        }
        catch (Exception ex) { Log.Warn("share files window: " + ex.Message); }
        finally { _checking = false; }
    }

    /// <summary>Card, Copy files and Open folder follow what was found; the texts say how many and what was left out.</summary>
    private void ShowState()
    {
        bool any = _present.Count > 0;
        Card.IsEnabled = CopyButton.IsEnabled = any;
        long size = _items.Where(i => _present.Contains(i.Path)).Sum(i => i.Size);
        int shown = any ? _present.Count : 0;
        NameText.Text = _looking && !any ? ClipRow.LookingText : $"{shown} {(shown == 1 ? "file" : "files")}" + (size > 0 ? " · " + Capabilities.FormatBytes(size) : "");
        MoreBadge.Visibility = shown > 1 ? Visibility.Visible : Visibility.Collapsed;
        MoreText.Text = "+" + (shown - 1);
        if (shown == 0) { ThumbImage.Source = null; _thumbFor = null; }
        DiscordText.Visibility = shown > DiscordLimit ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = any ? "Drag into Discord, a browser upload box or a folder" : _looking ? ClipRow.LookingText : "None of the files could be reached";
        if (any) HintText.ClearValue(TextBlock.ForegroundProperty); else if (!_looking) HintText.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        string? summary = _looking && !any ? null : BulkActions.ShareFilesSummary(_present.Count, _items.Count);
        if (summary != null) SetStatus(summary, "Warn"); else if (_statusIsSummary) SetStatus(null);
        _statusIsSummary = summary != null;
        ShowOverNote();
        var folders = _present.Select(p => Path.GetDirectoryName(p) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool oneFolder = any && folders == 1;
        FolderButton.IsEnabled = oneFolder;
        FolderButton.ToolTip = !any || oneFolder ? "Show the first file in its folder" : "The files are in different folders";
        System.Windows.Automation.AutomationProperties.SetHelpText(FolderButton, (string)FolderButton.ToolTip);
    }

    private bool _statusIsSummary;

    /// <summary>
    /// "2 files are over Discord free (20 MB): share them one at a time." when two or more of the files that were found (clips, not
    /// screenshots) are over the user's Discord plan, "1 file is over Discord free (20 MB): share it on its own." for one (its own
    /// Share window offers the smaller copy); null otherwise. No copies are made in this window.
    /// </summary>
    internal static string? OverNoteFor(IEnumerable<ShareFileItem> items, bool screenshots, ShareFitTarget target)
        => screenshots ? null : ShareFitModel.MultiFileNote(target, items.Count(i => ShareFitModel.StateFor(i.Size, target).Kind == ShareFitStateKind.Over));

    private void ShowOverNote()
    {
        string? note = null;
        try
        {
            if (AppController.Instance is { } app)
                note = OverNoteFor(_items.Where(i => _present.Contains(i.Path)), _screenshots, ShareFitModel.ForSettings(app.Settings));
        }
        catch (Exception ex) { Log.Warn("share files window: " + ex.Message); }
        bool changed = (note ?? "") != OverNote.Text;
        OverNote.Text = note ?? "";
        OverNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
        if (changed && note != null) { OverNoteAnnouncements++; ShareWindow.RaiseLive(OverNote); }   // LiveSetting alone announces nothing
    }

    /// <summary>How many times the note was announced to a screen reader (its text changed to some text); for the tests.</summary>
    internal int OverNoteAnnouncements { get; private set; }

    /// <summary>The controller's once-a-second tick: the Topmost rule, and the note follows a changed Discord plan while the window is open.</summary>
    internal void OnStateTick()
    {
        UpdateTopmost();
        if (!_closed) ShowOverNote();
    }

    /// <summary>The note currently shown (null when hidden).</summary>
    internal string? OverNoteText => OverNote.Visibility == Visibility.Visible ? OverNote.Text : null;

    private void SetStatus(string? text, string brush = "Accent2")
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    // ------------------------------------------------------------------ coming forward, and not over the game

    internal void BringForward(bool activate = true)
    {
        if (_closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _ = CheckAsync();
        if (activate && !QuietMode.Active) Activate();
    }

    /// <summary>Shows the window for a user who is over a game: without taking the focus, at the toasts' position on the captured monitor (as <see cref="ShareWindow"/> does).</summary>
    internal void ShowOverGame(MonitorInfo? monitor, string? toastPosition)
    {
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;
        Show();
        UpdateLayout();
        var wa = ScreenPlacement.WorkAreaOf(monitor);
        ScreenPlacement.Place(this, (w, h) => ShareWindow.ToastCorner(wa.left, wa.top, wa.right, wa.bottom, toastPosition, w, h, (int)Math.Round(ToastMargin * ScreenPlacement.ScaleOf(this))));
        Opacity = 1;
    }

    private void UpdateTopmost()
    {
        if (_closed || QuietMode.Active) return;
        ShareWindow.KeepBehindGame(this);
    }

    // ------------------------------------------------------------------ dragging the card out

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(null);
        _dragArmed = _present.Count > 0;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _dragArmed = false;

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragArmed = false; return; }
        var at = e.GetPosition(null);
        if (Math.Abs(at.X - _dragFrom.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(at.Y - _dragFrom.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        StartDrag();
    }

    private void StartDrag()
    {
        var paths = _present.ToList();
        if (paths.Count == 0) return;
        try
        {
            SetStatus(null);
            _statusIsSummary = false;
            var effect = DragDrop.DoDragDrop(Card, ShareWindow.FileData(paths), ShareWindow.DragEffects);
            if (effect == ShareWindow.DragEffects) SetStatus("Dropped. The originals stay in your library.");
            if ((effect & DragDropEffects.Move) != 0)
                Log.Warn($"share files window: a drop target answered {effect} to the copy-only drag of {paths.Count} files");
            _ = CheckAsync();   // a file a target took away disables its part of the card
        }
        catch (Exception ex)
        {
            Log.Warn("share files window: drag failed: " + ex.Message);
            SetStatus("Could not start the drag: " + ex.Message, "Warn");
        }
    }

    // ------------------------------------------------------------------ buttons

    private void CopyFiles_Click(object sender, RoutedEventArgs e) => CopyFiles();

    private void CopyFiles()
    {
        var paths = _present.ToList();
        if (paths.Count == 0) return;
        try
        {
            Clipboard.SetDataObject(ShareWindow.FileData(paths), true);
            _statusIsSummary = false;
            SetStatus($"{(paths.Count == 1 ? "File" : "Files")} copied. Paste with Ctrl+V into Discord or a folder.");
        }
        catch (Exception ex)
        {
            Log.Warn("share files window: clipboard: " + ex.Message);
            SetStatus("Could not copy the files: " + ex.Message, "Warn");
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!FolderButton.IsEnabled || _openingFolder || _present.Count == 0) return;
        _openingFolder = true;
        try
        {
            string path = _present[0];
            bool archived = _items.FirstOrDefault(i => i.Path == path)?.IsArchived == true;
            var state = await Task.Run(() => ShareWindow.Probe(path, archived));   // on a worker: a sleeping archive share must not freeze the window
            if (_closed) return;
            if (state != ShareWindow.FileState.Present) { _ = CheckAsync(); return; }
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Warn("share files window: open folder: " + ex.Message);
            SetStatus("Could not open the folder: " + ex.Message, "Warn");
        }
        finally { _openingFolder = false; }
    }

    private void SetUpCopium_Click(object sender, RoutedEventArgs e)
    {
        AppController.Instance.ShowCopiumSetup();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyFiles(); }
    }
}
