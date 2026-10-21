using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Clink.Services;

namespace Clink.UI;

internal sealed class ClipItem : INotifyPropertyChanged
{
    public ClipEntry Entry { get; }
    private BitmapSource? _thumb;
    private bool _uploading;
    private double _progress;
    private string? _status;

    public ClipItem(ClipEntry e) { Entry = e; }

    public string Title => string.IsNullOrEmpty(Entry.Game) ? "Clip" : Entry.Game!;
    public string Meta
    {
        get
        {
            string dur = Entry.Duration > 0 ? $"{TimeSpan.FromSeconds(Entry.Duration):m\\:ss} · " : "";
            return $"{Entry.Created:MMM d, HH:mm} · {dur}{Capabilities.FormatBytes(Entry.Size)}";
        }
    }
    public string ShareLabel => string.IsNullOrEmpty(Entry.CopiumLink) ? "Share" : "Copy link";
    public bool HasLink => !string.IsNullOrEmpty(Entry.CopiumLink);
    public BitmapSource? Thumbnail { get => _thumb; set { _thumb = value; Raise(); } }
    public bool IsUploading { get => _uploading; set { _uploading = value; Raise(); } }
    public double Progress { get => _progress; set { _progress = value; Raise(); } }
    public string? Status { get => _status; set { _status = value; Raise(); } }

    public void Refresh() { Raise(nameof(ShareLabel)); Raise(nameof(HasLink)); Raise(nameof(Meta)); Raise(nameof(Title)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
