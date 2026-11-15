namespace Clink.Capture;

/// <summary>
/// Where the engine gets video frames from: the most recent frame is kept in a GPU texture on the engine's D3D device,
/// and the pacer samples it. <see cref="MonitorCapture"/> is the implementation for a monitor.
/// </summary>
internal interface IVideoFrameSource : IDisposable
{
    /// <summary>Whether the source delivers scRGB (FP16) frames that the converter must tone-map.</summary>
    bool IsHdr { get; }
    /// <summary>Frames the source has delivered so far.</summary>
    long FramesReceived { get; }
    /// <summary>Frames copied into the latest-frame texture (a source may skip some it was handed).</summary>
    long FramesCopied { get; }
    /// <summary>Whether at least one frame has arrived.</summary>
    bool HasFrame { get; }

    /// <summary>
    /// Raised (signaled) when a new frame lands in the latest-frame texture, for sources that drive the pacing themselves.
    /// Null: the source has no such signal and the engine paces it on its own timer.
    /// </summary>
    WaitHandle? FrameArrived { get; }

    /// <summary>QPC time (100 ns units) of the latest frame; 0 before the first one.</summary>
    long LatestTime { get; }

    /// <summary>
    /// Runs <paramref name="use"/> with the latest frame's texture (pointer, width, height) while holding the device's
    /// context lock. Returns false, without calling it, when no frame has arrived yet.
    /// </summary>
    bool WithLatest(Action<nint, uint, uint> use);
}
