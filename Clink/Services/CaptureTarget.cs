using Clink.Capture;
using Clink.Native;

namespace Clink.Services;

/// <summary>What is captured: a screen, or a capture card.</summary>
internal enum CaptureKind { Monitor, Device }

/// <summary>Why the target is not the one the user chose: <see cref="CardMissing"/> = a card is chosen but not connected, so a screen is recorded until it is back.</summary>
internal enum CaptureFallback { None, CardMissing }

/// <summary>The resolved target. <see cref="Monitor"/> is set for a screen (null only when there is no screen at all); <see cref="Device"/> for a card.</summary>
internal sealed record CaptureChoice(CaptureKind Kind, VideoDevice? Device, MonitorInfo? Monitor, CaptureFallback Fallback)
{
    /// <summary>The same thing is captured: the same card, or the same screen.</summary>
    public bool SameTarget(CaptureChoice? other) =>
        other != null && Kind == other.Kind
        && (Kind == CaptureKind.Device
            ? string.Equals(Device?.Link, other.Device?.Link, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Monitor?.DeviceName, other.Monitor?.DeviceName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What the controller does when the resolved target changes.</summary>
internal enum SwitchAction { None, Restart, RestartAndNotifyFallback, RestartAndNotifyReturn }

/// <summary>The action, and whether a fallback notice has been shown for the card that is now missing (pass it back into the next <see cref="CaptureTarget.NextAction"/>).</summary>
internal readonly record struct SwitchDecision(SwitchAction Action, bool FallbackNoticed);

/// <summary>What is captured: the pure rules shared by the app (<see cref="AppController.SelectedMonitor"/>) and <c>--selftest</c>.</summary>
internal static class CaptureTarget
{
    public const string SourceMonitor = "monitor";
    private const string DevicePrefix = "device:";

    /// <summary>
    /// Following the Windows main display: the primary one (the first when none says it is). Otherwise the screen saved as
    /// <paramref name="monitorDevice"/> when it is connected, else the primary one, else the first. Null for an empty list.
    /// </summary>
    public static MonitorInfo? Resolve(bool followPrimary, string? monitorDevice, IReadOnlyList<MonitorInfo> monitors)
    {
        if (!followPrimary && !string.IsNullOrEmpty(monitorDevice))
        {
            var saved = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, monitorDevice, StringComparison.OrdinalIgnoreCase));
            if (saved != null) return saved;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    /// <summary>
    /// The target for the saved <see cref="Settings.CaptureSource"/>. A card that is connected: the card. A card that is chosen but not
    /// connected: the screen <see cref="Resolve(bool, string?, IReadOnlyList{MonitorInfo})"/> gives, marked <see cref="CaptureFallback.CardMissing"/>.
    /// A card that is connected but held by another app is still listed, so it stays the target (the capture retries; it is not a fallback).
    /// A source that is not "monitor" or "device:&lt;link&gt;" is the screen.
    /// </summary>
    public static CaptureChoice Resolve(string? captureSource, bool followPrimary, string? monitorDevice,
        IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<VideoDevice> devices)
    {
        string? link = ParseSource(captureSource);
        var monitor = Resolve(followPrimary, monitorDevice, monitors);
        if (link == null) return new CaptureChoice(CaptureKind.Monitor, null, monitor, CaptureFallback.None);
        var card = devices.FirstOrDefault(d => string.Equals(d.Link, link, StringComparison.OrdinalIgnoreCase));
        return card != null
            ? new CaptureChoice(CaptureKind.Device, card, null, CaptureFallback.None)
            : new CaptureChoice(CaptureKind.Monitor, null, monitor, CaptureFallback.CardMissing);
    }

    /// <summary>The card's symbolic link in a <see cref="Settings.CaptureSource"/> value; null for a screen (missing, "monitor", an empty link, anything else).</summary>
    public static string? ParseSource(string? captureSource)
    {
        if (captureSource == null || !captureSource.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        string link = captureSource[DevicePrefix.Length..].Trim();
        return link.Length == 0 ? null : link;
    }

    /// <summary>The <see cref="Settings.CaptureSource"/> value for a card (<c>device:&lt;link&gt;</c>); an empty link gives "monitor".</summary>
    public static string FormatSource(string? deviceLink) => string.IsNullOrWhiteSpace(deviceLink) ? SourceMonitor : DevicePrefix + deviceLink.Trim();

    /// <summary>
    /// What to do now that the target is <paramref name="next"/> and was <paramref name="prev"/> (null: nothing runs yet, so the target is
    /// started). The same target: nothing. A different one: restart on it. Falling back to a screen because the card is missing also
    /// asks for the notice, but only once per stretch of the card being missing (<paramref name="fallbackNoticed"/> is the
    /// <see cref="SwitchDecision.FallbackNoticed"/> of the previous call): a screen change while the card is still missing restarts
    /// quietly. When the card is back after a fallback the "back" notice is asked for and the stretch ends.
    /// </summary>
    public static SwitchDecision NextAction(CaptureChoice? prev, CaptureChoice next, bool fallbackNoticed)
    {
        bool missing = next.Fallback == CaptureFallback.CardMissing;
        bool wasMissing = prev?.Fallback == CaptureFallback.CardMissing;
        if (!missing) fallbackNoticed = false;   // the stretch is over (or never began)
        bool changed = !next.SameTarget(prev);
        if (missing)
        {
            if (!fallbackNoticed) return new SwitchDecision(SwitchAction.RestartAndNotifyFallback, true);
            return new SwitchDecision(changed ? SwitchAction.Restart : SwitchAction.None, true);
        }
        if (wasMissing && next.Kind == CaptureKind.Device) return new SwitchDecision(SwitchAction.RestartAndNotifyReturn, false);
        return new SwitchDecision(changed ? SwitchAction.Restart : SwitchAction.None, false);
    }

    /// <summary>The value of <see cref="Settings.FollowPrimaryMonitor"/> for a file that does not have the key: a screen was never picked (null) means the main display was followed.</summary>
    public static bool MigratedFollowPrimary(string? monitorDevice) => monitorDevice == null;
}
