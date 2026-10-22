using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Clink.Services;

namespace Clink.UI;

/// <summary>
/// The share window: what Share opens for a clip when there is no Copium token to upload it with (<see cref="AppController.ShareAsync"/>).
/// Its file card is the clip's file, ready to drag out into Discord, a browser upload box or an Explorer folder; Copy file puts the
/// same file on the clipboard for Ctrl+V; Open folder shows it in Explorer; Set up Copium… brings the main window forward to add a
/// token. Dragging and copying only ever <b>copy</b> (<see cref="FileData"/>), so a drop target can never move the clip out of the
/// library. An archived clip is looked up on a worker first (its share may be offline): the card is disabled, saying so, while the
/// file cannot be reached. The window floats above other windows (Topmost) so it stays in reach while the drop target is brought
/// forward, has no owner (minimizing or hiding the main window does not take it along), and stays out of screen captures like
/// the toasts do, so it never ends up in a clip. Colours are all DynamicResources.
/// </summary>
public partial class ShareWindow : Window
{
    private const string DragHint = "Drag into Discord, a browser upload box or a folder";
    /// <summary>The clipboard / drag format Explorer reads to decide between copy and move.</summary>
    internal const string PreferredDropEffect = "Preferred DropEffect";

    private enum FileState { Unknown, Present, Missing, Offline }

    private FileState _state;
    private bool _checking, _closed, _dragArmed, _stateNote;
    private Point _dragFrom;

    internal ClipEntry Entry { get; }

    internal ShareWindow(ClipEntry entry)
    {
        Entry = entry;
        InitializeComponent();
        NameText.Text = entry.FileName;
        NameText.ToolTip = entry.Path;
        MetaText.Text = MetaFor(entry.Size, entry.Duration);
        // A local file is taken to be there (the worker's look is quick and only matters when it is not); an archived one waits for
        // its share to answer, which can take the SMB timeout when it is offline.
        _state = entry.IsArchived ? FileState.Unknown : FileState.Present;
        ShowState();
        Loaded += (_, _) => _ = CheckAsync();
        // Back at the window after the share was down (or the file was missing): look again, quietly.
        Activated += (_, _) => { if (_state is FileState.Offline or FileState.Missing) _ = CheckAsync(); };
        // Topmost and on the captured monitor: keep it out of the clips, like the toasts and the status dot.
        SourceInitialized += (_, _) => ScreenPlacement.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        Closed += (_, _) => _closed = true;
    }

    // ------------------------------------------------------------------ what is dragged and copied

    /// <summary>
    /// The clip's file as something to drag or paste: a file drop (what Explorer, Discord and browser upload boxes take), allowed
    /// to be <b>copied only</b>. "Preferred DropEffect" = Copy tells Explorer's paste the same, and the drag itself offers no
    /// other effect, so no target can move the clip out of the library.
    /// </summary>
    internal static DataObject FileData(string path)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        data.SetData(PreferredDropEffect, new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
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

    /// <summary>Looks at the file on a worker (and makes its thumbnail the first time), then enables or disables the card.</summary>
    private async Task CheckAsync()
    {
        if (_checking || _closed) return;
        _checking = true;
        // Looking again: a card that is off stays off with "Looking in the archive…"; one that is on is left alone (a drag may be starting).
        if (_state != FileState.Present) { _state = FileState.Unknown; ShowState(); }
        string path = Entry.Path;
        bool archived = Entry.IsArchived, needThumb = ThumbImage.Source == null;
        FileState state;
        BitmapSource? thumb;
        try
        {
            (state, thumb) = await Task.Run(() =>
            {
                var s = Probe(path, archived);
                return (s, s == FileState.Present && needThumb ? Thumbnails.Get(path, 176, 100) : null);
            });
        }
        finally { _checking = false; }
        if (_closed) return;
        _state = state;
        if (thumb != null) ThumbImage.Source = thumb;
        ShowState();
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
    /// Accent2 by default, not Accent: the accent as text measures under 4.5:1 on the light theme for Cyan and Orange.
    /// </summary>
    private void SetStatus(string? text, string brush = "Accent2")
    {
        StatusText.Text = text ?? "";
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    /// <summary>Brings an open window forward: Share was pressed again for the same clip.</summary>
    internal void BringForward()
    {
        if (_closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
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
            var effect = DragDrop.DoDragDrop(Card, FileData(Entry.Path), DragDropEffects.Copy);
            if (effect == DragDropEffects.Copy) SetStatus("Dropped. The clip stays in your library.");
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

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_state == FileState.Present) AppController.Instance.OpenFolder(Entry.Path);
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
