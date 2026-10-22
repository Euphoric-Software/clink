using System.Windows;

namespace Clink.UI;

/// <summary>
/// The Clink icon (Assets/clink.ico) where nothing draws it by itself: the tray. The exe carries it as its ApplicationIcon and the
/// main and trim windows set it as their Icon.
/// </summary>
internal static class AppIcon
{
    private static readonly Uri Source = new("pack://application:,,,/Assets/clink.ico");

    /// <summary>
    /// The notification-area icon: the frame of the .ico that matches the small-icon size (16 px at 100 % scale, 20 / 24 / 32 at higher
    /// scales), so the shell does not have to stretch it. Falls back to the exe's own icon.
    /// </summary>
    public static System.Drawing.Icon Tray()
    {
        try
        {
            var res = Application.GetResourceStream(Source);
            if (res != null)
            {
                using var stream = res.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex) { Log.Warn("tray icon: " + ex.Message); }
        return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application;
    }
}
