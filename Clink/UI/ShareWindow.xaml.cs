using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Clink.Native;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The share window: what Share opens for a clip when there is no Copium token to upload it with (<see cref="AppController.ShareAsync"/>).
/// Its file card is the clip's file, ready to drag out into Discord, a browser upload box or an Explorer folder; Copy file puts the
/// same file on the clipboard for Ctrl+V; Open folder shows it in Explorer; Set up Copium… brings the main window forward to add a
/// token. Dragging and copying only ever <b>copy</b> (<see cref="DragEffects"/>, <see cref="FileData"/>), so a drop target can never
/// move the clip out of the library. The file is looked at on a worker, never on the UI thread (an archive share may be offline or
/// asleep): when the window opens, whenever it comes forward again (the clip may have been archived or replaced by a trim since) and
/// before Open folder; the card is disabled, saying why, while the file cannot be reached.
/// <para>
/// The window floats above other windows (Topmost) so it stays in reach while the drop target is brought forward, except over a game:
/// it goes behind the game while that has the focus, and opens without taking the focus, at the toasts' position, when a game is
/// running (<see cref="ShowOverGame"/>). It has no owner (minimizing or hiding the main window does not take it along), and it and
/// its tooltips stay out of screen captures like the toasts do (<see cref="ICaptureExcludedWindow"/>), so it never ends up in a
/// clip. It shows no file path anywhere. Colours are all DynamicResources.
/// </para>
/// </summary>
public partial class ShareWindow : Window, ICaptureExcludedWindow
{
    private const string DragHint = "Drag into Discord, a browser upload box or a folder";
    /// <summary>The clipboard / drag format Explorer reads to decide between copy and move.</summary>
    internal const string PreferredDropEffect = "Preferred DropEffect";
    /// <summary>
    /// What a drop target may do with the dragged file: <b>copy it</b>, nothing else. Move would let Explorer move the clip out of
    /// the library. The drag allows exactly this, and the "Preferred DropEffect" of the data says the same.
    /// </summary>
    internal const DragDropEffects DragEffects = DragDropEffects.Copy;
    /// <summary>The toasts' distance from the edges of the work area, in DIPs (<c>Toasts</c>).</summary>
    private const int ToastMargin = 16;

    private enum FileState { Unknown, Present, Missing, Offline }

    private FileState _state;
    private bool _checking, _closed, _dragArmed, _stateNote, _afterDrag, _openingFolder;
    private long _thumbSize;       // the clip's size when the thumbnail on the card was made: another size is another file
    private string? _thumbMissFor; // "path|size" of a file the shell could make no thumbnail for: not asked again until it changes
    private Point _dragFrom;

    internal ClipEntry Entry { get; }

    /// <summary>The tooltips of this window are kept out of capture too, whichever window class is the first one shown (<see cref="CaptureExclusion"/>).</summary>
    static ShareWindow() => CaptureExclusion.Register();

    internal ShareWindow(ClipEntry entry)
    {
        Entry = entry;
        InitializeComponent();
        AppIcons.Attach(this);   // has its own taskbar button (no owner): the icon size the taskbar draws, not WPF's default frame
        // Cloud sharing off (Settings → App): the file to drag is local, but there is nothing to set up.
        if (CloudSharingOff()) SetUpCopiumButton.Visibility = Visibility.Collapsed;
        ShowEntry();
        // A local file is taken to be there (the worker's look is quick and only matters when it is not); an archived one waits for
        // its share to answer, which can take the SMB timeout when it is offline.
        _state = entry.IsArchived ? FileState.Unknown : FileState.Present;
        ShowState();
        Loaded += (_, _) => _ = CheckAsync();
        // Whenever the window comes forward (Share pressed again, the user came back to it): look at the clip again, quietly. It may
        // have been archived, replaced by a trim or deleted since, and a share that was down may be back.
        Activated += (_, _) => { if (!QuietMode.Active) Topmost = true; _ = CheckAsync(); };
        // Not over a game: once the window has lost the focus to the game it goes behind it (Topmost is back when it is activated, or
        // when the game loses the focus; the controller's once-a-second state tick catches a game that took the focus without
        // this window ever having had it).
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateTopmost));
        SourceInitialized += (_, _) =>
        {
            // Topmost and on the captured monitor: keep it out of the clips, like the toasts and the status dot.
            ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
            AppController.Instance.StateChanged += UpdateTopmost;   // once there is a window; it is let go of when it closes
        };
        Closed += (_, _) =>
        {
            _closed = true;
            AppController.Instance.StateChanged -= UpdateTopmost;
        };
    }

    private static bool CloudSharingOff() => AppController.Instance?.Settings.CloudSharingEnabled == false;

    // ------------------------------------------------------------------ what is dragged and copied

    /// <summary>
    /// The clip's file as something to drag or paste: a file drop (what Explorer, Discord and browser upload boxes take), allowed
    /// to be <b>copied only</b> (<see cref="DragEffects"/>). "Preferred DropEffect" = Copy tells Explorer's paste the same, and the
    /// drag itself offers no other effect, so no target can move the clip out of the library.
    /// </summary>
    internal static DataObject FileData(string path)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes((int)DragEffects)));
        return data;
    }

    /// <summary>"190 MB · 1:30": the size, then the length when it is known (a clip found in the folder has none).</summary>
    internal static string MetaFor(long size, double duration)
    {
        var parts = new List<string>(2);
        if (size > 0) parts.Add(Capabilities.FormatBytes(size));
        if (duration > 0) parts.Add(Capabilities.FormatClipLength(duration));
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ the file behind the card

    /// <summary>Any thread, never the UI one for an archived path: an offline share blocks for its SMB timeout. Same reading as the clip rows.</summary>
    private static FileState Probe(string path, bool archived)
    {
        string? dir = Path.GetDirectoryName(path);
        try
        {
            if (!archived) return File.Exists(path) ? FileState.Present : FileState.Missing;
            if (dir == null || !Directory.Exists(dir)) return FileState.Offline;
            // Not File.Exists: it says false for any error (a dropped SMB session, access denied while the share signs in again).
            // Only the server's "no such file" with the folder still answering is a deleted clip.
            File.GetAttributes(path);
            return FileState.Present;
        }
        catch (FileNotFoundException)
        {
            try { return dir != null && Directory.Exists(dir) ? FileState.Missing : FileState.Offline; }
            catch { return FileState.Offline; }
        }
        catch { return FileState.Offline; }
    }

    /// <summary>
    /// Name, size and length from the entry as it is now (an archive move changes the path, a trim that replaced the original the
    /// size and length), and the thumbnail dropped when the size changed: it is another file then.
    /// </summary>
    private void ShowEntry()
    {
        NameText.Text = Entry.FileName;
        MetaText.Text = MetaFor(Entry.Size, Entry.Duration);
        if (_thumbSize != Entry.Size) ThumbImage.Source = null;
    }

    /// <summary>
    /// Looks at the clip again: the entry's name, size and length; then the file, on a worker, which enables or disables the card at
    /// once; then, apart from that, its thumbnail (on an archive share that takes a while, and the card must not wait for it).
    /// Runs again when the entry changed while it was looking, or a drag ended meanwhile. One look at a time.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_checking || _closed) return;
        _checking = true;
        try
        {
            while (!_closed)
            {
                ShowEntry();
                string path = Entry.Path;
                bool archived = Entry.IsArchived;
                long size = Entry.Size;
                bool afterDrag = _afterDrag;   // this look starts after a drag ended: a gone file is worth a warning
                _afterDrag = false;
                // Looking again: a card that is off stays off with "Looking in the archive…"; one that is on is left alone (a drag may be starting).
                if (_state != FileState.Present) { _state = FileState.Unknown; ShowState(); }
                var state = await Task.Run(() => Probe(path, archived));
                if (_closed) return;
                if (afterDrag && state != FileState.Present)
                    Log.Warn($"share window: after the drag {Path.GetFileName(path)} is {state}: a drop target may have moved it out of the library");
                _state = state;
                ShowState();   // usable as soon as the file is known to be there, before its thumbnail is made
                string key = path + "|" + size;
                if (state == FileState.Present && ThumbImage.Source == null && _thumbMissFor != key)
                {
                    BitmapSource? thumb = await Task.Run(() => Thumbnails.Get(path, 176, 100));
                    // Not when the clip became another file while it was made (the next look makes it again).
                    if (!_closed && Entry.Size == size)
                    {
                        if (thumb != null && ThumbImage.Source == null) { ThumbImage.Source = thumb; _thumbSize = size; }
                        else if (thumb == null) _thumbMissFor = key;
                    }
                }
                if (_afterDrag) continue;   // a drag ended while this look was running
                if (Entry.Path == path && Entry.IsArchived == archived && Entry.Size == size) break;   // nothing changed meanwhile
            }
        }
        finally { _checking = false; }
    }

    /// <summary>Card, Copy file and Open folder work only while the file is there; the hint under the name says why not.</summary>
    private void ShowState()
    {
        bool present = _state == FileState.Present;
        Card.IsEnabled = CopyButton.IsEnabled = FolderButton.IsEnabled = present;
        switch (_state)
        {
            case FileState.Present:
                HintText.Text = DragHint;
                HintText.ClearValue(TextBlock.ForegroundProperty);
                if (_stateNote) { SetStatus(null); _stateNote = false; }
                return;
            case FileState.Unknown:
                HintText.Text = ClipRow.LookingText;
                HintText.ClearValue(TextBlock.ForegroundProperty);
                return;
            case FileState.Offline:
                HintText.Text = ClipRow.ArchiveOffline;
                SetStatus("Clink looks again when you come back to this window.", "Muted");
                _stateNote = true;
                break;
            default:
                HintText.Text = Entry.IsArchived ? ClipRow.DeletedFromArchiveText : ClipRow.LocalDeleted;
                SetStatus(null);
                break;
        }
        HintText.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
    }

    /// <summary>
    /// The line under the card; <paramref name="brush"/> is a palette key (a DynamicResource reference, so it follows the theme).
    /// Accent2 by default, not Accent: the accent as text fails 4.5:1 on the light theme for several accents (e.g. Orange, Pink).
    /// </summary>
    private void SetStatus(string? text, string brush = "Accent2")
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    // ------------------------------------------------------------------ coming forward, and not over the game

    /// <summary>
    /// Brings an open window forward and looks at the clip again: Share was pressed for the same clip once more. Without
    /// <paramref name="activate"/> (over a game) the window is only made visible, it takes no focus.
    /// </summary>
    internal void BringForward(bool activate = true)
    {
        if (_closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        _ = CheckAsync();
        if (activate && !QuietMode.Active) Activate();
    }

    /// <summary>
    /// Shows the window for a user who is over a game (a toast over it was clicked, or it has the focus): without taking the focus,
    /// and at the toasts' position on the captured monitor (<paramref name="toastPosition"/>, "bottom-right" and so on), not in the
    /// middle of the picture.
    /// </summary>
    internal void ShowOverGame(MonitorInfo? monitor, string? toastPosition)
    {
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;                     // shown invisible, placed, then made visible: no flash at the wrong spot (like the toasts)
        Show();
        UpdateLayout();
        var wa = ScreenPlacement.WorkAreaOf(monitor);
        ScreenPlacement.Place(this, (w, h) => ToastCorner(wa.left, wa.top, wa.right, wa.bottom, toastPosition, w, h, (int)Math.Round(ToastMargin * ScreenPlacement.ScaleOf(this))));
        Opacity = 1;
    }

    /// <summary>
    /// Where a window of this size goes at a toast position of a work area ("top-left", "top", "top-right", "left", "right",
    /// "bottom-left", "bottom", "bottom-right"; none set means bottom-right), <paramref name="margin"/> pixels from the edges:
    /// the toasts' own rule (<c>Toasts.Layout</c>), so a side the name does not give is the middle. Physical pixels. Pure.
    /// </summary>
    internal static (int x, int y) ToastCorner(int left, int top, int right, int bottom, string? position, int w, int h, int margin)
    {
        string pos = (position ?? "bottom-right").Trim().ToLowerInvariant();
        bool atTop = pos.StartsWith("top", StringComparison.Ordinal), atBottom = pos.StartsWith("bottom", StringComparison.Ordinal);
        bool atLeft = pos.EndsWith("left", StringComparison.Ordinal), atRight = pos.EndsWith("right", StringComparison.Ordinal);
        int x = atLeft ? left + margin : atRight ? right - margin - w : (left + right - w) / 2;
        int y = atTop ? top + margin : atBottom ? bottom - margin - h : (top + bottom - h) / 2;
        return (x, y);
    }

    /// <summary>
    /// Topmost keeps the window in reach while the drop target is brought forward, but never over the game: while the detected game
    /// has the focus the window goes behind it, and comes back on top once the game has lost the focus.
    /// </summary>
    private void UpdateTopmost()
    {
        if (_closed || QuietMode.Active) return;   // --dev-quiet: never topmost
        bool game = Hotkeys.GameInFront();
        if (game && Topmost)
        {
            Topmost = false;
            // Not enough on its own: a window that stops being topmost stays above the ordinary windows, the game's included. Slip it in
            // right behind the game's window (the foreground one).
            var fg = PInvoke.GetForegroundWindow();
            nint self = new WindowInteropHelper(this).Handle;
            if (!fg.IsNull && self != 0)
                PInvoke.SetWindowPos(new HWND(self), fg, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
        else if (!game && !Topmost) Topmost = true;
    }

    // ------------------------------------------------------------------ dragging the card out

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(null);
        _dragArmed = _state == FileState.Present;
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
        try
        {
            SetStatus(null);
            // Copy only: allowing Move would let Explorer move the clip out of the library.
            var effect = DragDrop.DoDragDrop(Card, FileData(Entry.Path), DragEffects);
            if (effect == DragEffects) SetStatus("Dropped. The clip stays in your library.");
            // The guard is the allowed effect above; if a target still answers Move, or the file is gone afterwards, the clip may
            // have left the library: say so in the log, and look at the card again (a gone file disables it).
            if ((effect & DragDropEffects.Move) != 0)
                Log.Warn($"share window: a drop target answered {effect} to the copy-only drag of {Entry.FileName}");
            _afterDrag = true;
            _ = CheckAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("share window: drag failed: " + ex.Message);
            SetStatus("Could not start the drag: " + ex.Message, "Warn");
        }
    }

    // ------------------------------------------------------------------ buttons

    private void CopyFile_Click(object sender, RoutedEventArgs e) => CopyFile();

    private void CopyFile()
    {
        if (_state != FileState.Present) return;
        try
        {
            Clipboard.SetDataObject(FileData(Entry.Path), true);
            SetStatus("File copied. Paste it with Ctrl+V into Discord or a folder.");
        }
        catch (Exception ex)
        {
            Log.Warn("share window: clipboard: " + ex.Message);
            SetStatus("Could not copy the file: " + ex.Message, "Warn");
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync();

    /// <summary>
    /// Shows the file in Explorer. The file is looked at again on a worker first, so a share that went to sleep cannot freeze the
    /// window (on the UI thread that look would block for the SMB timeout) and a clip that was archived or deleted since the last
    /// look is not acted on; Explorer is then started on the path itself, not through <see cref="AppController.OpenFolder"/>, which
    /// tests the file on the UI thread and falls back to the clips folder.
    /// </summary>
    private async Task OpenFolderAsync()
    {
        if (_state != FileState.Present || _openingFolder) return;
        _openingFolder = true;
        try
        {
            string path = Entry.Path;
            bool archived = Entry.IsArchived;
            SetStatus("Looking for the file…", "Muted");   // an archive share that is waking up can take a while
            var state = await Task.Run(() => Probe(path, archived));
            if (_closed) return;
            if (state != FileState.Present) { _state = state; ShowState(); return; }
            SetStatus(null);
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Warn("share window: open folder: " + ex.Message);
            SetStatus("Could not open the folder: " + ex.Message, "Warn");
        }
        finally { _openingFolder = false; }
    }

    private void SetUpCopium_Click(object sender, RoutedEventArgs e)
    {
        // The main window comes forward with the Copium card to fill in; this window would only sit on top of it.
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
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyFile(); }
    }
}
