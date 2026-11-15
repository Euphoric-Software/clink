using Clink.Native;

namespace Clink.Services;

/// <summary>Which screen is captured: the pure rule shared by the app (<see cref="AppController.SelectedMonitor"/>) and <c>--selftest</c>.</summary>
internal static class CaptureTarget
{
    /// <summary>
    /// Following the Windows main display: the primary one (the first when none says it is). Otherwise the screen saved as
    /// <paramref name="monitorDevice"/> when it is connected, else the primary one, else the first. Null for an empty list.
    /// </summary>
    public static MonitorInfo? Resolve(bool followPrimary, string? monitorDevice, IReadOnlyList<MonitorInfo> monitors)
    {
        if (!followPrimary)
        {
            var saved = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, monitorDevice, StringComparison.OrdinalIgnoreCase));
            if (saved != null) return saved;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    /// <summary>The value of <see cref="Settings.FollowPrimaryMonitor"/> for a file that does not have the key: a screen was never picked (null) means the main display was followed.</summary>
    public static bool MigratedFollowPrimary(string? monitorDevice) => monitorDevice == null;
}
