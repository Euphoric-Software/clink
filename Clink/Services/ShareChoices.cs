namespace Clink.Services;

/// <summary>What a video clip's Share button can do when it opens a menu.</summary>
internal enum ShareChoice { Upload, CopyLink, ShareFile, SaveCopy }

/// <summary>One menu entry: <paramref name="Header"/> carries its access key (underscore), <paramref name="ToolTip"/> says what it does.</summary>
internal sealed record ShareChoiceItem(ShareChoice Choice, string Header, string ToolTip);

/// <summary>
/// The Share button of a video clip (clip list row, clip-saved toast): with a Copium token and cloud sharing on it opens a menu
/// (upload / copy link, share the file, save a copy); otherwise it does what it always did. Pure.
/// </summary>
internal static class ShareChoices
{
    /// <summary>The menu to show, or null when the button acts directly (no token, or cloud sharing off).</summary>
    public static IReadOnlyList<ShareChoiceItem>? For(bool hasToken, bool cloudOn, bool hasLink)
    {
        if (!hasToken || !cloudOn) return null;
        return new[]
        {
            hasLink
                ? new ShareChoiceItem(ShareChoice.CopyLink, "_Copy link", "Copy the clip's Copium link")
                : new ShareChoiceItem(ShareChoice.Upload, "_Upload to Copium", "Upload to your Copium account and copy the link"),
            new ShareChoiceItem(ShareChoice.ShareFile, "_Share file…", "Drag or copy the file into Discord, a browser or a folder"),
            new ShareChoiceItem(ShareChoice.SaveCopy, "S_ave a copy…", "Save a copy of the clip file to another folder"),
        };
    }

    /// <summary>The label of a menu button.</summary>
    public const string MenuLabel = "Share ▾";

    /// <summary>The clip row's Share button: the menu label, else today's "Copy link" / "Share".</summary>
    public static string ButtonLabel(bool hasToken, bool cloudOn, bool hasLink) =>
        For(hasToken, cloudOn, hasLink) != null ? MenuLabel : hasLink ? "Copy link" : "Share";

    /// <summary>The clip-saved toast's Share button: the menu label, else today's "Copy link" / "Share to Copium" / "Share".</summary>
    public static string ToastLabel(bool hasToken, bool cloudOn, bool hasLink) =>
        For(hasToken, cloudOn, hasLink) != null ? MenuLabel : hasLink ? "Copy link" : hasToken ? "Share to Copium" : "Share";

    /// <summary>The accessible name of a menu button; null when it acts directly.</summary>
    public static string? ButtonName(bool hasToken, bool cloudOn, bool hasLink) =>
        For(hasToken, cloudOn, hasLink) != null ? "Share options" : null;
}
