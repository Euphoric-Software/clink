namespace Clink.Engine;

/// <summary>
/// What the share-copy job (<c>AppController.MakeShareCopyAsync</c>) needs from the engine: a header-only look at a clip, the
/// re-encode and the stream copy. <see cref="DefaultShareTranscoder"/> is the real thing (the static <see cref="ShareTranscoder"/>
/// and <see cref="ShareProbe"/>); the unit tests and the Copium upload tests (2e) put a fake in <c>AppController.ShareTranscoderImpl</c>.
/// The contracts of <see cref="ShareTranscoder.TranscodeAsync"/> and <see cref="ShareTranscoder.RemuxMixOnlyAsync"/> apply: a failure is a
/// <see cref="ShareTranscodeException"/>, a cancel an <see cref="OperationCanceledException"/>, and nothing is left behind at the output
/// path on either.
/// </summary>
internal interface IShareTranscoder
{
    /// <summary>The clip's length, codec, size, frame rate and audio track count (<see cref="ShareProbe.Read"/>).</summary>
    ShareProbeResult Probe(string path, double keyframeSeconds);

    Task<ShareTranscodeResult> TranscodeAsync(string sourcePath, string outputPath, ShareTranscodeRequest request, IProgress<double>? progress, CancellationToken ct);

    Task<ShareTranscodeResult> RemuxMixOnlyAsync(string sourcePath, string outputPath, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>The real <see cref="IShareTranscoder"/>: a thin adapter over the static <see cref="ShareTranscoder"/> and <see cref="ShareProbe"/>.</summary>
internal sealed class DefaultShareTranscoder : IShareTranscoder
{
    public static readonly DefaultShareTranscoder Instance = new();

    public ShareProbeResult Probe(string path, double keyframeSeconds) => ShareProbe.Read(path, keyframeSeconds);

    public Task<ShareTranscodeResult> TranscodeAsync(string sourcePath, string outputPath, ShareTranscodeRequest request, IProgress<double>? progress, CancellationToken ct)
        => ShareTranscoder.TranscodeAsync(sourcePath, outputPath, request, progress, ct);

    public Task<ShareTranscodeResult> RemuxMixOnlyAsync(string sourcePath, string outputPath, IProgress<double>? progress, CancellationToken ct)
        => ShareTranscoder.RemuxMixOnlyAsync(sourcePath, outputPath, progress, ct);
}
