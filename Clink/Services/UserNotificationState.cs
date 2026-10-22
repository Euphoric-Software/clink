using System.Runtime.InteropServices;

namespace Clink.Services;

/// <summary>
/// The shell's answer to "may a program put something over what the user is doing?" (SHQueryUserNotificationState). It catches what
/// Clink's own game detection (<see cref="Hotkeys.GameInFront"/>) does not: a Direct3D game in exclusive full screen that was
/// not detected, another full-screen program, presentation mode. Used where nothing may pop up over the game (a Share from the
/// hotkey with no Copium token opens no window).
/// </summary>
internal static class UserNotificationState
{
    // QUERY_USER_NOTIFICATION_STATE (shellapi.h): 1 not present, 2 busy, 3 running D3D full screen, 4 presentation mode,
    // 5 accepts notifications, 6 quiet time, 7 app.
    private const int Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>A full-screen application (or presentation mode) holds the screen: <c>QUNS_BUSY</c>, <c>QUNS_RUNNING_D3D_FULL_SCREEN</c> or <c>QUNS_PRESENTATION_MODE</c>. Pure.</summary>
    internal static bool IsFullScreenLike(int state) => state is Busy or RunningD3DFullScreen or PresentationMode;

    /// <summary>The shell reports a full-screen application in front. False when it cannot say. Any thread; a few microseconds.</summary>
    public static bool FullScreenInFront()
    {
        try { return SHQueryUserNotificationState(out int state) == 0 && IsFullScreenLike(state); }
        catch { return false; }
    }
}
