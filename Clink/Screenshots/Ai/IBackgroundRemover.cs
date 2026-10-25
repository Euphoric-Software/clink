using Clink.Screenshots.Imaging;

namespace Clink.Screenshots.Ai;

/// <summary>
/// The seam for an AI "remove background" tool in the screenshot editor. NOT wired to anything in this version: there is no toolbar
/// button, no model and no download. <see cref="BackgroundRemovers.Current"/> is <see cref="UnavailableBackgroundRemover"/>.
/// <para>
/// Planned shape: the result is an <see cref="AlphaMask"/> that becomes a raster object applied last in the raster band (a
/// transparent PNG on export; undo comes with the document's undo stack). The model is downloaded on demand, never shipped: an https
/// URL, a pinned SHA-256 and the size and licence shown to the user for explicit consent first, resumable (.partial) as the updater does.
/// A runtime (ONNX Runtime) would be a second package and a native DLL inside the single-file exe: a separate decision.
/// </para>
/// <para>
/// Licences (re-check each weights file at implementation time): RMBG-1.4 is non-commercial and must never be the default
/// download; u2net, ISNet and MODNet (Apache-2.0) and BiRefNet (MIT) are permissive.
/// </para>
/// </summary>
internal interface IBackgroundRemover
{
    /// <summary>True when <see cref="RemoveBackgroundAsync"/> can run now (a model is present and loaded).</summary>
    bool IsAvailable { get; }

    /// <summary>Why it cannot run (shown as the tooltip of a disabled button); null when <see cref="IsAvailable"/>.</summary>
    string? UnavailableReason { get; }

    /// <summary>The foreground mask of <paramref name="image"/> (255 = keep). <paramref name="progress"/> goes 0..1.</summary>
    Task<AlphaMask> RemoveBackgroundAsync(BgraImage image, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>The default: this version has no background remover.</summary>
internal sealed class UnavailableBackgroundRemover : IBackgroundRemover
{
    public const string Reason = "Not available in this version";

    public bool IsAvailable => false;
    public string? UnavailableReason => Reason;

    public Task<AlphaMask> RemoveBackgroundAsync(BgraImage image, IProgress<double>? progress, CancellationToken ct) =>
        Task.FromException<AlphaMask>(new NotSupportedException(Reason));
}

/// <summary>Where the editor would find the active remover. A later version replaces <see cref="Current"/> once a model store exists.</summary>
internal static class BackgroundRemovers
{
    public static IBackgroundRemover Current { get; set; } = new UnavailableBackgroundRemover();
}
