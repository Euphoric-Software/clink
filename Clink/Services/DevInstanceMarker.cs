using System.IO;

namespace Clink.Services;

/// <summary>
/// How a <c>--dev &lt;folder&gt;</c> test instance announces itself, so nobody takes it for the main Clink: the texts of the window title, the
/// header badge, its tooltip, the tray tooltip and the tag on other windows, and the rule for which of them gives way when the header is
/// short of room. Pure text and arithmetic (no window, no <see cref="AppController"/> state): the windows read <see cref="AppController.IsDev"/>
/// and <see cref="Settings.Dir"/> and call these.
/// </summary>
internal static class DevInstanceMarker
{
    /// <summary>The test instance's colour, #FFD400 (the main window's and toasts' border, the badge's fill).</summary>
    public const string YellowHex = "#FFD400";

    private const int MaxLeaf = 28;
    private const string Tag = " [TEST]";

    /// <summary>The last part of the dev folder ("C:\work\clink-test\" gives "clink-test"); a drive root gives the drive, nothing at all gives "dev".</summary>
    public static string FolderLeaf(string? folder)
    {
        string f = (folder ?? "").Trim();
        if (f.Length == 0) return "dev";
        string leaf;
        try
        {
            string trimmed = f.TrimEnd('\\', '/');
            if (trimmed.Length == 0) trimmed = f;
            leaf = Path.GetFileName(trimmed);
            if (leaf.Length == 0) leaf = trimmed;   // "C:\" or "C:": the drive is the name
        }
        catch (ArgumentException) { leaf = f; }
        leaf = leaf.Trim();
        if (leaf.Length == 0) return "dev";
        return leaf.Length > MaxLeaf ? leaf[..(MaxLeaf - 1)] + "…" : leaf;
    }

    /// <summary>The main window's title (the taskbar button): "Clink TEST (clink-test)".</summary>
    public static string WindowTitle(string? folder) => $"Clink TEST ({FolderLeaf(folder)})";

    /// <summary>The header badge: "TEST · clink-test", or just "TEST" when there is no room for the folder name.</summary>
    public static string BadgeText(string? folder, bool shortForm) => shortForm ? "TEST" : "TEST · " + FolderLeaf(folder);

    /// <summary>The badge's tooltip, with the full folder.</summary>
    public static string BadgeTooltip(string? folder) => $"Test instance (--dev {(folder ?? "").Trim()}). Your main Clink is separate.";

    /// <summary>The tray icon's tooltip, marked.</summary>
    public static string TrayText(string text) => "TEST · " + text;

    /// <summary>A title with the test tag at its end ("Trim clip [TEST]"); once only, and an empty title stays empty (it has none to mark).</summary>
    public static string TagTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return title ?? "";
        return title.EndsWith(Tag, StringComparison.Ordinal) ? title : title + Tag;
    }

    /// <summary>
    /// What the header's status group shows in <paramref name="available"/> device-independent pixels, after the dot and "Clink"
    /// (<paramref name="lead"/>). The badge outranks the status text: the status text is dropped first (when it would have less than
    /// <paramref name="minStatus"/> left), and only then is the badge cut to "TEST". Without a badge the status text is always shown
    /// (it ellipsizes by itself).
    /// </summary>
    public static (bool ShowStatus, bool ShortBadge) FitHeader(double available, double lead, double fullBadge, double minStatus, bool hasBadge)
    {
        if (!hasBadge) return (true, false);
        double room = available - lead;
        if (room >= fullBadge + minStatus) return (true, false);
        if (room >= fullBadge) return (false, false);
        return (false, true);
    }
}
