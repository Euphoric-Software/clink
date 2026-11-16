using System.Windows.Threading;
using Clink.UI;

namespace Clink.Services;

// "What's new" at start: what WhatsNewPolicy says to show, held back while a game or full-screen app is in front (looked at again every
// 15 s, then shown once), and the update menu's link that opens the whole changelog. UI thread.
internal sealed partial class AppController
{
    /// <summary>How often a held-back "What's new" looks again for a game or full-screen app in front.</summary>
    internal static readonly TimeSpan WhatsNewRecheck = TimeSpan.FromSeconds(15);

    private IReadOnlyList<ChangelogSection>? _whatsNewPending;   // decided at start, not shown yet (a game was in front)
    private bool _whatsNewActivate;
    private DispatcherTimer? _whatsNewTimer;

    /// <summary>
    /// Test seam: what puts the window on screen (<see cref="WhatsNewWindow.ShowFor"/>'s <c>show</c>). Null: <c>Show()</c>, refused in a
    /// unit-test run. A test gets the real window, built and never shown, and calls <see cref="WhatsNewWindow.MarkShown"/> as Loaded would.
    /// </summary>
    internal Action<System.Windows.Window>? WhatsNewShow { get; set; }

    /// <summary>A "What's new" waits for the screen to be free of a game (for the tests).</summary>
    internal bool WhatsNewWaiting => _whatsNewPending != null;

    /// <summary>The 15 s re-check is running (for the tests).</summary>
    internal bool WhatsNewTimerRunning => _whatsNewTimer?.IsEnabled == true;

    /// <summary>
    /// Once, after the start (App): decides what the window shows on its own (<see cref="WhatsNewPolicy.Decide"/>: nothing in a --dev
    /// instance or a unit-test run) and shows it, or holds it back while a game is in front. <paramref name="firstInstall"/>: there was no
    /// settings file and nothing was imported. <paramref name="activate"/> false at a start in the tray: it does not take the focus.
    /// </summary>
    internal void StartWhatsNew(bool firstInstall, bool activate)
    {
        IReadOnlyList<ChangelogSection> sections;
        try { sections = WhatsNewPolicy.Decide(WhatsNew.Current, Updater.Current, IsDev, TestGuard.Active, firstInstall, Settings.WhatsNewSeen, Settings.WhatsNewSeenDevHash); }
        catch (Exception ex) { Log.Warn("what's new: " + ex.Message); return; }
        if (sections.Count == 0) return;
        Log.Info("what's new: to show " + string.Join(", ", sections.Select(s => s.Heading)) + (firstInstall ? " (first install)" : ""));
        ScheduleWhatsNew(sections, activate);
    }

    /// <summary>
    /// Shows <paramref name="sections"/> now when no game or full-screen app is in front; otherwise looks again every
    /// <see cref="WhatsNewRecheck"/>. A show after waiting never takes the focus: the user is busy in whatever is in front by then.
    /// </summary>
    internal void ScheduleWhatsNew(IReadOnlyList<ChangelogSection> sections, bool activate)
    {
        if (_disposed) return;
        _whatsNewPending = sections;
        _whatsNewActivate = activate;
        if (TryShowPendingWhatsNew()) return;
        _whatsNewActivate = false;   // deferred: shown without activating
        Log.Info("what's new: a game or full-screen app is in front; shown once there is none");
        _whatsNewTimer ??= new DispatcherTimer(WhatsNewRecheck, DispatcherPriority.Background, (_, _) => TryShowPendingWhatsNew(), _ui);
        _whatsNewTimer.Start();
    }

    /// <summary>The re-check (the timer's tick): shows the held-back sections once nothing is in front. True when it showed them.</summary>
    internal bool TryShowPendingWhatsNew()
    {
        if (_whatsNewPending is not { } sections || _disposed) { StopWhatsNewTimer(); return false; }
        bool inFront;
        try { inFront = GameOrFullScreenInFront(); }
        catch (Exception ex) { Log.Warn("what's new: game check: " + ex.Message); inFront = true; }   // unsure: wait for the next look
        if (inFront) return false;
        _whatsNewPending = null;
        StopWhatsNewTimer();
        OpenWhatsNewWindow(sections, _whatsNewActivate);
        return true;
    }

    /// <summary>The update menu's "What's new": the whole changelog, in front. An automatic one still waiting is not needed any more.</summary>
    internal void OpenWhatsNew()
    {
        if (WhatsNew.Current is not { } log) { Log.Warn("what's new: this build has no readable changelog"); return; }
        _whatsNewPending = null;
        StopWhatsNewTimer();
        OpenWhatsNewWindow(log.Sections, activate: true);
    }

    private void OpenWhatsNewWindow(IReadOnlyList<ChangelogSection> sections, bool activate)
    {
        try
        {
            if (WhatsNewShow == null) TestGuard.CheckLaunch("the what's new window");   // before the window exists
            WhatsNewWindow.ShowFor(System.Windows.Application.Current?.MainWindow, sections, Updater.Current, activate, MarkWhatsNewSeen, WhatsNewShow);
        }
        catch (Exception ex) { Log.Error("what's new window", ex); }
    }

    /// <summary>The window is on screen: this version (and on a dev build its notes) counts as seen.</summary>
    private void MarkWhatsNewSeen()
    {
        if (WhatsNewPolicy.MarkSeen(Settings, WhatsNew.Current, Updater.Current)) Log.Info("what's new: seen up to v" + Settings.WhatsNewSeen);
    }

    private void StopWhatsNewTimer() => _whatsNewTimer?.Stop();
}
