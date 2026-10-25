using Clink.Services;

namespace Clink.Screenshots;

/// <summary>
/// The proactive "Share with Copium?" pop-up (S7; it replaced the S6 toast hint): pure rules plus the counters in
/// <see cref="Settings"/>. It is offered after the <see cref="AfterScreenshots"/>nd saved screenshot on an install that has not agreed
/// to sharing and has not said "Don't ask again", at most <see cref="MaxProactive"/> times in all and at least <see cref="MinGap"/>
/// apart, and never while cloud sharing is off. The pop-up itself and the "a game is in front" check belong to the caller.
/// </summary>
internal static class CopiumNudge
{
    public const int AfterScreenshots = 2;
    public const int MaxProactive = 2;
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(24);

    /// <summary>
    /// True when the pop-up is due. A last showing in the FUTURE (the clock went back) holds nothing back for ever:
    /// <see cref="IsDue"/> starts the gap from now.
    /// </summary>
    public static bool ShouldPrompt(bool consent, bool dontAsk, bool cloudOn, int screenshots, int promptCount, DateTime? lastPromptUtc, DateTime nowUtc)
    {
        if (consent || dontAsk || !cloudOn) return false;
        if (screenshots < AfterScreenshots || promptCount >= MaxProactive) return false;
        if (lastPromptUtc is DateTime last && nowUtc - last.ToUniversalTime() < MinGap) return false;   // (a negative gap too: see IsDue)
        return true;
    }

    /// <summary><see cref="ShouldPrompt"/> for the settings as they are now.</summary>
    public static bool IsDue(Settings s, DateTime nowUtc)
    {
        // The clock went back past the last showing: it would hold the pop-up back until the clock caught up. Start the gap now instead.
        if (s.CopiumConsentPromptedUtc is DateTime last && last.ToUniversalTime() > nowUtc)
        {
            s.CopiumConsentPromptedUtc = nowUtc;
            Persist(s);
            return false;
        }
        return ShouldPrompt(s.CopiumConsent, s.CopiumConsentDontAsk, s.CloudSharingEnabled, s.CopiumConsentScreenshots, s.CopiumConsentPromptCount, s.CopiumConsentPromptedUtc, nowUtc);
    }

    /// <summary>
    /// A screenshot was saved: counted while the install is still undecided (the count stops at <see cref="AfterScreenshots"/> and is
    /// saved only when it changes). Nothing is counted once sharing is agreed to or "Don't ask again" was chosen.
    /// </summary>
    public static void CountScreenshot(Settings s)
    {
        if (s.CopiumConsent || s.CopiumConsentDontAsk || s.CopiumConsentScreenshots >= AfterScreenshots) return;
        s.CopiumConsentScreenshots++;
        Persist(s);
    }

    /// <summary>Counts one unasked showing (saved to the settings file; a disk problem is logged, never thrown).</summary>
    public static void MarkPrompted(Settings s, DateTime nowUtc)
    {
        s.CopiumConsentPromptCount = Math.Min(MaxProactive, Math.Max(0, s.CopiumConsentPromptCount) + 1);
        s.CopiumConsentPromptedUtc = nowUtc;
        Persist(s);
    }

    private static void Persist(Settings s)
    {
        try { s.Save(); }
        catch (Exception ex) { Log.Warn("copium consent: settings not saved: " + ex.Message); }
    }
}
