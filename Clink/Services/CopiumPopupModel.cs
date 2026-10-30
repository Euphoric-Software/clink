namespace Clink.Services;

/// <summary>Which Copium entry the version-number popup shows.</summary>
internal enum CopiumPopupKind
{
    /// <summary>Cloud sharing is off: one muted line, no toggle.</summary>
    CloudOff,
    /// <summary>Signed in with "Sign in with Copium": "Copium: {Username}" and the toggle.</summary>
    Connect,
    /// <summary>A pasted token: "Copium: API token" and the toggle.</summary>
    PastedToken,
    /// <summary>No token: the "Sign in with Copium" link (opens the App tab card, starts nothing).</summary>
    SignedOut,
}

/// <summary>
/// The Copium section at the end of the version-number popup, without WPF. The toggle is the Screenshots tab's automatic-upload box in a second
/// place: the window routes it through that box, so the consent pop-up, the kill switch and the saved setting are the same.
/// </summary>
internal sealed class CopiumPopupModel
{
    internal const string ToggleLabel = "Auto-upload screenshots";
    internal const string SignInLink = "Sign in with Copium";
    internal const string PastedTokenLine = "Copium: API token";

    public CopiumPopupKind Kind { get; init; }
    /// <summary>The account line (plain text) or the muted cloud-off line; null for the sign-in link.</summary>
    public string? Line { get; init; }
    public bool LineMuted { get; init; }
    public bool ToggleVisible { get; init; }
    /// <summary>Same rule as the Screenshots tab box: a token and cloud sharing on (consent is asked when it is ticked, not here).</summary>
    public bool ToggleEnabled { get; init; }
    /// <summary>What the Screenshots tab box shows: the setting and the user's yes (<c>MainWindow.ScreenshotChecksFor</c>).</summary>
    public bool ToggleChecked { get; init; }
    public bool LinkVisible { get; init; }

    /// <param name="screenshotUploadShown">The Screenshots tab box's value for these settings (setting AND consent).</param>
    public static CopiumPopupModel From(SignInState state, bool cloudSharingOn, bool screenshotUploadShown)
    {
        if (!cloudSharingOn) return new CopiumPopupModel { Kind = CopiumPopupKind.CloudOff, Line = ConsentGate.CloudOffText, LineMuted = true };
        if (state.HasToken && state.LinkedViaConnect)
            return new CopiumPopupModel
            {
                Kind = CopiumPopupKind.Connect, Line = "Copium: " + AccountName(state.Username),
                ToggleVisible = true, ToggleEnabled = true, ToggleChecked = screenshotUploadShown,
            };
        if (state.HasToken)
            return new CopiumPopupModel
            {
                Kind = CopiumPopupKind.PastedToken, Line = PastedTokenLine,
                ToggleVisible = true, ToggleEnabled = true, ToggleChecked = screenshotUploadShown,
            };
        return new CopiumPopupModel { Kind = CopiumPopupKind.SignedOut, LinkVisible = true };
    }

    private static string AccountName(string? username)
    {
        string name = (username ?? "").Trim();
        if (name.Length == 0) return "signed in";
        if (name.Length > CopiumWelcomeModel.MaxNameChars) name = name[..CopiumWelcomeModel.MaxNameChars].TrimEnd() + "…";
        return CopiumCardModel.Iso(name);
    }
}
