using Clink.Capture;
using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>One entry of the Source picker: the <see cref="Settings.CaptureSource"/> value it saves, the card's name (null for the screen) and what it reads.</summary>
internal sealed record SourceItem(string Source, string? CardName, string Label);

/// <summary>What the Source picker lists and what its detail line says. Pure (no window, no device access): unit-tested.</summary>
internal static class CaptureSourceList
{
    /// <summary>
    /// "Screen", then each card in the order Windows lists them. The saved card is labelled by <paramref name="savedCardMissing"/> when the
    /// controller knows (true: "&lt;name&gt; (not connected)" even if a stale listing still has it; false: the plain name, even if the listing
    /// lacks it); when null (unknown) by the listing: not in <paramref name="devices"/> = not connected, no listing yet = the name alone.
    /// A saved card that is not listed stays in the list, selected.
    /// </summary>
    public static List<SourceItem> Build(IReadOnlyList<VideoDevice>? devices, string? savedSource, string? savedName, bool? savedCardMissing)
    {
        var items = new List<SourceItem> { new(CaptureTarget.SourceMonitor, null, L.T("Screen")) };
        string? savedLink = CaptureTarget.ParseSource(savedSource);
        bool savedListed = false;
        bool missing = savedCardMissing ?? false;
        foreach (var d in devices ?? Array.Empty<VideoDevice>())
        {
            if (string.IsNullOrEmpty(d.Link)) continue;
            bool isSaved = savedLink != null && string.Equals(d.Link, savedLink, StringComparison.OrdinalIgnoreCase);
            if (items.Any(i => string.Equals(CaptureTarget.ParseSource(i.Source), d.Link, StringComparison.OrdinalIgnoreCase))) continue;   // a device listed twice
            savedListed |= isSaved;
            items.Add(new SourceItem(CaptureTarget.FormatSource(d.Link), d.Name,
                isSaved && savedCardMissing == true ? L.T("{name} (not connected)", ("name", L.Isolate(d.Name))) : L.Isolate(d.Name)));
        }
        if (savedLink != null && !savedListed)
        {
            string name = string.IsNullOrWhiteSpace(savedName) ? L.T("Capture card") : savedName.Trim();
            if (savedCardMissing == null) missing = devices != null;
            items.Add(new SourceItem(CaptureTarget.FormatSource(savedLink), string.IsNullOrWhiteSpace(savedName) ? null : name,
                missing ? L.T("{name} (not connected)", ("name", L.Isolate(name))) : L.Isolate(name)));
        }
        return items;
    }

    /// <summary>
    /// What the controller knows of the saved card: true when its last start fell back to a screen for it, false when the card is what it
    /// resolved to, null when it resolved something else (an older source, not started yet): the listing decides then.
    /// </summary>
    public static bool? SavedCardMissing(CaptureChoice? current, string? savedSource)
    {
        string? link = CaptureTarget.ParseSource(savedSource);
        if (link == null || current == null) return null;
        if (current.Fallback == CaptureFallback.CardMissing && string.Equals(current.MissingLink, link, StringComparison.OrdinalIgnoreCase)) return true;
        if (current.Kind == CaptureKind.Device && string.Equals(current.Device?.Link, link, StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>
    /// The line under the picker while a card is the source: why it does not record when it does not (in use, blocked, not connected),
    /// else its mode (size, rate, format), with the HDR warning when the card sends HDR. Null when there is nothing to say yet.
    /// </summary>
    public static string? Detail(bool cardMissing, string? problem, VideoMode? mode)
    {
        if (cardMissing) return L.T("Not connected: recording the fallback screen instead");
        if (problem != null) return problem;
        if (mode is not { } m) return null;
        string size = L.Ltr($"{m.Width}×{m.Height}"), fps = L.Ltr(m.Fps.ToString("0.##", L.FormatCulture)), format = L.Ltr(FormatName(m.Format));
        return m.Hdr
            ? L.T("{size}  ·  {fps} fps  ·  {format}  ·  HDR, recorded as SDR", ("size", size), ("fps", fps), ("format", format))
            : L.T("{size}  ·  {fps} fps  ·  {format}", ("size", size), ("fps", fps), ("format", format));
    }

    /// <summary>The pixel layout as a person reads it (these are technical names, the same in every language).</summary>
    internal static string FormatName(VideoFormat f) => f switch
    {
        VideoFormat.Nv12 => "NV12",
        VideoFormat.Yuy2 => "YUY2",
        VideoFormat.P010 => "P010",
        VideoFormat.Rgb32 => "RGB",
        VideoFormat.Mjpg => "MJPEG",
        _ => "?",
    };
}
