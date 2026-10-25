using Clink.Services;

namespace Clink.Screenshots;

/// <summary>
/// The "link your Copium account" hint after a screenshot was saved without a token: pure rules plus the counters in
/// <see cref="Settings"/>. It never blocks or asks anything: a notification with two buttons (Set up Copium / Don't show again),
/// shown at most <see cref="MaxShown"/> times in all and at most once every <see cref="MinGap"/>, and never again once dismissed.
/// </summary>
internal static class CopiumNudge
{
    public const int MaxShown = 3;
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(24);

    public const string Title = "Saved locally";
    public const string Text = "Link your Copium account to share screenshots automatically";

    /// <summary>
    /// True when the hint is due: only without a token, with upload on, not dismissed, shown fewer than <see cref="MaxShown"/> times and
    /// not within <see cref="MinGap"/> of the last time. A last time in the FUTURE (the clock went back) shows nothing:
    /// <see cref="IsDue"/> sets it to now, so the gap runs from here.
    /// </summary>
    public static bool ShouldShow(bool hasToken, bool uploadEnabled, bool dismissed, int shownCount, DateTime? lastShownUtc, DateTime nowUtc)
    {
        if (hasToken || !uploadEnabled || dismissed || shownCount >= MaxShown) return false;
        if (lastShownUtc is DateTime last)
        {
            var since = nowUtc - last.ToUniversalTime();
            if (since < MinGap) return false;   // (negative too: see IsDue)
        }
        return true;
    }

    /// <summary><see cref="ShouldShow"/> for the settings as they are now.</summary>
    public static bool IsDue(Settings s, bool hasToken, DateTime nowUtc)
    {
        // The clock went back past the last showing: it would hold the hint back until the clock caught up. Start the gap now instead.
        if (s.CopiumNudgeLastShownUtc is DateTime last && last.ToUniversalTime() > nowUtc)
        {
            s.CopiumNudgeLastShownUtc = nowUtc;
            Persist(s);
            return false;
        }
        return ShouldShow(hasToken, s.ScreenshotUpload, s.CopiumNudgeDismissed, s.CopiumNudgeShown, s.CopiumNudgeLastShownUtc, nowUtc);
    }

    /// <summary>Counts one showing (saved to the settings file; a disk problem is logged, never thrown).</summary>
    public static void MarkShown(Settings s, DateTime nowUtc)
    {
        s.CopiumNudgeShown = Math.Min(MaxShown, Math.Max(0, s.CopiumNudgeShown) + 1);
        s.CopiumNudgeLastShownUtc = nowUtc;
        Persist(s);
    }

    /// <summary>"Don't show again".</summary>
    public static void Dismiss(Settings s)
    {
        s.CopiumNudgeDismissed = true;
        Persist(s);
    }

    private static void Persist(Settings s)
    {
        try { s.Save(); }
        catch (Exception ex) { Log.Warn("copium nudge: settings not saved: " + ex.Message); }
    }
}
