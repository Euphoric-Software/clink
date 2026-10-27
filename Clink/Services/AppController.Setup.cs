namespace Clink.Services;

// First-run setup: the controller's side ("Run setup again" in the app).
internal sealed partial class AppController
{
    /// <summary>
    /// "Run setup again": starts Clink again with <c>--setup</c> for that one start (the wizard shows whatever the settings say; a later restart
    /// does not carry the switch, see <see cref="RestartArguments"/>) and ends this process, like any restart (<see cref="RestartClink"/>: the new
    /// Clink waits for this one). True when the new Clink was started. On failure nothing is given up, the user is told, and Clink carries on.
    /// UI thread (any other is marshalled; the result is then false).
    /// </summary>
    public bool RunSetupAgain()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(() => RunSetupAgain()); return false; }
        try { Settings.Save(); } catch (Exception ex) { Log.Warn("setup: settings not saved before the restart: " + ex.Message); }
        if (RestartClink(out string? error, new[] { SetupArgs.Setup })) return true;
        string why = $"Clink could not restart itself to run setup ({error}).";
        Say(why);
        ShowBalloon("Clink", why);
        return false;
    }
}
