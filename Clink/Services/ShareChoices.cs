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

    // The split button (where there is a menu): a main "Share" half that opens the share window, and a "▾" half that opens the menu.

    /// <summary>The label of the main half of the split button.</summary>
    public const string SplitMainLabel = "Share";
    /// <summary>The label of the arrow half.</summary>
    public const string SplitArrowLabel = "▾";
    /// <summary>The accessible name of the main half (it opens the share window).</summary>
    public const string SplitMainName = "Share file";
    /// <summary>The accessible name of the arrow half (it opens the menu).</summary>
    public const string SplitArrowName = "Share options";
    public const string SplitMainTip = "Open the share window: drag the file into Discord, a browser or a folder";
    public const string SplitArrowTip = "More share options";
    /// <summary>What the main half runs: the same as the menu's "Share file…".</summary>
    public const ShareChoice SplitMainChoice = ShareChoice.ShareFile;

    // The trim editor's "Save & share": split under the same gate as the rows (a token and cloud sharing on).

    /// <summary>The trim editor's "Save &amp; share" is split (a token and cloud sharing on); otherwise it is the plain button (hidden when cloud sharing is off).</summary>
    public static bool TrimSplit(bool hasToken, bool cloudOn) => IsSplit(hasToken, cloudOn, false);

    /// <summary>
    /// The trim editor's menu, or null when the button stays plain. The choices are applied to the trim once it is saved, so the saved
    /// clip is a new, unlinked one (even with Replace original the link is cleared): "Copy link" never applies, and every label says
    /// the trim is saved first (the window's other button is "Save trimmed", so a bare "Share file…" would not).
    /// </summary>
    public static IReadOnlyList<ShareChoiceItem>? ForTrim(bool hasToken, bool cloudOn)
    {
        if (For(hasToken, cloudOn, false) == null) return null;
        return new[]
        {
            new ShareChoiceItem(ShareChoice.Upload, "Save & _upload to Copium", "Save the trim, then upload it to your Copium account and copy the link"),
            new ShareChoiceItem(ShareChoice.ShareFile, "Save & s_hare file…", "Save the trim, then drag or copy the file into Discord, a browser or a folder"),
            new ShareChoiceItem(ShareChoice.SaveCopy, "Save & save a _copy…", "Save the trim, then save a copy of the file to another folder"),
        };
    }

    /// <summary>The row's Share button shows at all: for a clip with a link, else while cloud sharing is on.</summary>
    public static bool Visible(bool cloudOn, bool hasLink) => hasLink || cloudOn;

    /// <summary>The plain (unsplit) Share button shows; never together with the split one.</summary>
    public static bool PlainVisible(bool hasToken, bool cloudOn, bool hasLink) => Visible(cloudOn, hasLink) && !IsSplit(hasToken, cloudOn, hasLink);

    /// <summary>The button is split (a menu exists): a token and cloud sharing on.</summary>
    public static bool IsSplit(bool hasToken, bool cloudOn, bool hasLink) => For(hasToken, cloudOn, hasLink) != null;

    /// <summary>The clip row's Share button (the main half when split): "Share", else today's "Copy link" / "Share".</summary>
    public static string ButtonLabel(bool hasToken, bool cloudOn, bool hasLink) =>
        IsSplit(hasToken, cloudOn, hasLink) ? SplitMainLabel : hasLink ? "Copy link" : "Share";

    /// <summary>The clip-saved toast's Share button (the main half when split): "Share", else today's "Copy link" / "Share to Copium" / "Share".</summary>
    public static string ToastLabel(bool hasToken, bool cloudOn, bool hasLink) =>
        IsSplit(hasToken, cloudOn, hasLink) ? SplitMainLabel : hasLink ? "Copy link" : hasToken ? "Share to Copium" : "Share";

    /// <summary>The accessible name of the main half when split; null when the button acts directly (its label names it).</summary>
    public static string? ButtonName(bool hasToken, bool cloudOn, bool hasLink) =>
        IsSplit(hasToken, cloudOn, hasLink) ? SplitMainName : null;
}
