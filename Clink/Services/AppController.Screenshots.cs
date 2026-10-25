using Clink.Screenshots;
using Clink.UI.Screenshots;

namespace Clink.Services;

// Screenshots: the controller's side (hotkey / tray / CLI glue). The subsystem itself is Clink.Screenshots.ScreenshotService;
// the hotkey hook thread only ever reaches it through _ui.BeginInvoke(OnHotkey), so the overlay windows and the clipboard stay on the UI thread.
internal sealed partial class AppController
{
    private ScreenshotService? _screenshots;
    private EditorWindowHost? _screenshotEditor;

    /// <summary>The screenshot coordinator (created on first use, on the UI thread).</summary>
    internal ScreenshotService Screenshots =>
        _screenshots ??= new ScreenshotService(Settings, Toasts, new RegionOverlayPicker((title, text) => Toasts.ShowNotice(title, text)), () => CurrentGame, () => SelectedMonitor, editor: _screenshotEditor = new EditorWindowHost());

    /// <summary>Opens the screenshots folder in Explorer (created first when it does not exist yet).</summary>
    public void OpenScreenshotsFolder()
    {
        try
        {
            string folder = ScreenshotService.FolderFor(Settings);
            System.IO.Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception ex) { Say("Could not open the screenshots folder: " + ex.Message); }
    }

    /// <summary>The keys of the first screenshot row that is set, for labels ("Take screenshot (PrintScreen)"); null when there is none.</summary>
    internal static string? ScreenshotHotkeyLabelFor(IReadOnlyList<HotkeyAction> hotkeys) =>
        hotkeys.FirstOrDefault(h => h.Keys != null && h.Keys.Vk > 0 && CommandFor(h.Action) == HotkeyCommand.Screenshot)?.Keys.ToString();

    /// <summary>"Take screenshot (PrintScreen)" for the tray menu, or just "Take screenshot" without a binding.</summary>
    private string ScreenshotTrayText => ScreenshotHotkeyLabelFor(Settings.Hotkeys) is string l ? $"Take screenshot ({l})" : "Take screenshot";

    /// <summary>The keys of the first "screenshot-edit-last" row that is set; null when there is none.</summary>
    internal static string? ScreenshotEditHotkeyLabelFor(IReadOnlyList<HotkeyAction> hotkeys) =>
        hotkeys.FirstOrDefault(h => h.Keys != null && h.Keys.Vk > 0 && CommandFor(h.Action) == HotkeyCommand.ScreenshotEditLast)?.Keys.ToString();

    /// <summary>"Edit last screenshot (Shift+PrintScreen)" for the tray menu, or just "Edit last screenshot" without a binding.</summary>
    private string ScreenshotEditTrayText => ScreenshotEditHotkeyLabelFor(Settings.Hotkeys) is string l ? $"Edit last screenshot ({l})" : "Edit last screenshot";

    /// <summary>The command a pipe / command-line request <c>screenshot-edit &lt;path&gt;</c> carries: its path, or null when it is not that command.</summary>
    internal static string? EditPathOf(string cmd)
    {
        const string verb = "screenshot-edit";
        string t = cmd.Trim();
        if (t.Length <= verb.Length + 1 || !t.StartsWith(verb, StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(t[verb.Length])) return null;
        string path = t[(verb.Length + 1)..].Trim().Trim('"');
        return path.Length == 0 ? null : path;
    }
}
