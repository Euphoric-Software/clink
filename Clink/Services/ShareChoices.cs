using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>What a video clip's Share button can do when it opens a menu.</summary>
internal enum ShareChoice { Upload, CopyLink, ShareFile, SaveCopy }

/// <summary>One menu entry: <paramref name="Header"/> carries its access key (underscore), <paramref name="ToolTip"/> says what it does. Both are in the UI language already.</summary>
internal sealed record ShareChoiceItem(ShareChoice Choice, string Header, string ToolTip);

/// <summary>
/// The Share button of a video clip. The clip list row is always split (<see cref="ForLibrary"/>); the clip-saved toast and the trim
/// editor are split only with a Copium token and cloud sharing on (<see cref="For"/>, <see cref="ForTrim"/>), otherwise the button does
/// what it always did. Pure. The texts are looked up when asked
/// for (the labels are properties, not constants), so they follow the UI language; callers compare labels only against labels from here.
/// </summary>
internal static class ShareChoices
{
    /// <summary>The toast's menu, or null when the button acts directly (no token, or cloud sharing off).</summary>
    public static IReadOnlyList<ShareChoiceItem>? For(bool hasToken, bool cloudOn, bool hasLink)
    {
        if (!hasToken || !cloudOn) return null;
        return new[]
        {
            hasLink
                ? new ShareChoiceItem(ShareChoice.CopyLink, L.T("_Copy link"), L.T("Copy the clip's Copium link"))
                : new ShareChoiceItem(ShareChoice.Upload, L.T("_Upload to Copium"), L.T("Upload to your Copium account and copy the link")),
            new ShareChoiceItem(ShareChoice.ShareFile, L.T("_Share file…"), L.T("Drag or copy the file into Discord, a browser or a folder")),
            new ShareChoiceItem(ShareChoice.SaveCopy, L.T("S_ave a copy…"), L.T("Save a copy of the clip file to another folder")),
        };
    }

    /// <summary>
    /// The clip list row's menu: always there (the row's Share button is always split). "Copy link" for a clip with a link, else
    /// "Upload to Copium" only with a token and cloud sharing on (without a token the main half's share window already explains
    /// Copium; with cloud off an upload is declined), then "Share file…" and "Save a copy…" whatever the account state.
    /// </summary>
    public static IReadOnlyList<ShareChoiceItem> ForLibrary(bool hasToken, bool cloudOn, bool hasLink)
    {
        var items = new List<ShareChoiceItem>(3);
        if (hasLink) items.Add(new ShareChoiceItem(ShareChoice.CopyLink, L.T("_Copy link"), L.T("Copy the clip's Copium link")));
        else if (hasToken && cloudOn) items.Add(new ShareChoiceItem(ShareChoice.Upload, L.T("_Upload to Copium"), L.T("Upload to your Copium account and copy the link")));
        items.Add(new ShareChoiceItem(ShareChoice.ShareFile, L.T("_Share file…"), L.T("Drag or copy the file into Discord, a browser or a folder")));
        items.Add(new ShareChoiceItem(ShareChoice.SaveCopy, L.T("S_ave a copy…"), L.T("Save a copy of the clip file to another folder")));
        return items;
    }

    // The split button (where there is a menu): a main "Share" half that opens the share window, and a "▾" half that opens the menu.

    /// <summary>The label of the main half of the split button.</summary>
    public static string SplitMainLabel => L.T("Share");
    /// <summary>The label of the arrow half (a glyph, not a word).</summary>
    public const string SplitArrowLabel = "▾";
    /// <summary>The accessible name of the main half (it opens the share window).</summary>
    public static string SplitMainName => L.T("Share file");
    /// <summary>The accessible name of the arrow half (it opens the menu).</summary>
    public static string SplitArrowName => L.T("Share options");
    public static string SplitMainTip => L.T("Open the share window: drag the file into Discord, a browser or a folder");
    public static string SplitArrowTip => L.T("More share options");
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
            new ShareChoiceItem(ShareChoice.Upload, L.T("Save & _upload to Copium"), L.T("Save the trim, then upload it to your Copium account and copy the link")),
            new ShareChoiceItem(ShareChoice.ShareFile, L.T("Save & s_hare file…"), L.T("Save the trim, then drag or copy the file into Discord, a browser or a folder")),
            new ShareChoiceItem(ShareChoice.SaveCopy, L.T("Save & save a _copy…"), L.T("Save the trim, then save a copy of the file to another folder")),
        };
    }

    /// <summary>The toast's button is split (a menu exists): a token and cloud sharing on.</summary>
    public static bool IsSplit(bool hasToken, bool cloudOn, bool hasLink) => For(hasToken, cloudOn, hasLink) != null;

    /// <summary>The clip-saved toast's Share button (the main half when split): "Share", else today's "Copy link" / "Share to Copium" / "Share".</summary>
    public static string ToastLabel(bool hasToken, bool cloudOn, bool hasLink) =>
        IsSplit(hasToken, cloudOn, hasLink) ? SplitMainLabel : hasLink ? L.T("Copy link") : hasToken ? L.T("Share to Copium") : L.T("Share");

    /// <summary>The accessible name of the main half when split; null when the button acts directly (its label names it).</summary>
    public static string? ButtonName(bool hasToken, bool cloudOn, bool hasLink) =>
        IsSplit(hasToken, cloudOn, hasLink) ? SplitMainName : null;
}
