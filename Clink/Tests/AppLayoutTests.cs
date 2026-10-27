using System.IO;
using Clink.Services;

namespace Clink.Tests;

// Dev / installed / standalone layout (AppLayout; "portable" is just the bare exe) and the settings file it must leave unchanged. Pure: temp dirs only.
internal static class AppLayoutTests
{
    private const string Local = @"C:\Users\Test\AppData\Local";
    private static AppLayout At(string? exe, bool dev = false) => AppLayout.Resolve(exe, dev, Local);

    [UnitTest]
    public static void Layout_standalone_is_todays_behaviour()
    {
        var l = At(@"D:\Tools\Clink.exe");
        Check.Equal(AppMode.Standalone, l.Mode);
        Check.Equal(@"D:\Tools", l.ExeDir);
        Check.Equal(Path.Combine(Local, "Clink", "updates"), l.UpdatesDir);
        Check.Equal(AppMode.Standalone, At(null).Mode);
        Check.Null(At(null).ExeDir);
        Check.Equal(AppMode.Standalone, At("   ").Mode, "blank path");
    }

    [UnitTest]
    public static void Layout_a_marker_file_beside_the_exe_changes_nothing()
    {
        // The removed marker-based portable mode must not come back by accident: a Clink.portable file is just a file.
        using var dir = new TempDir();
        File.WriteAllText(dir.File("Clink.portable"), "");
        var l = AppLayout.Resolve(dir.File("Clink.exe"), false, Local);
        Check.Equal(AppMode.Standalone, l.Mode);
        Check.Equal(Path.Combine(Local, "Clink", "updates"), l.UpdatesDir);
    }

    [UnitTest]
    public static void Layout_dev_wins_over_installed()
    {
        string installed = Path.Combine(Local, "Programs", "Clink", "Clink.exe");
        var dev = At(installed, dev: true);
        Check.Equal(AppMode.Dev, dev.Mode, "--dev beats the installed location");
        Check.Equal(Path.Combine(Local, "Clink", "updates"), dev.UpdatesDir);
        Check.Equal(AppMode.Installed, At(installed).Mode);
    }

    [UnitTest]
    public static void Layout_installed_matches_normalised_and_case_insensitive()
    {
        string dir = Path.Combine(Local, "Programs", "Clink");
        Check.Equal(AppMode.Installed, At(Path.Combine(dir, "Clink.exe")).Mode);
        Check.Equal(AppMode.Installed, At(dir.ToUpperInvariant() + @"\Clink.exe").Mode, "case");
        Check.Equal(AppMode.Installed, At(dir + @"\.\Clink.exe").Mode, "dot segment");
        Check.Equal(AppMode.Installed, At(Path.Combine(Local, "Programs", "Other", "..", "Clink", "Clink.exe")).Mode, "dotdot");
        Check.Equal(AppMode.Installed, At(dir + "/Clink.exe").Mode, "forward slash");
        Check.Equal(AppMode.Standalone, At(Path.Combine(dir, "bin", "Clink.exe")).Mode, "a subfolder is not it");
        Check.Equal(AppMode.Standalone, At(dir + "2" + @"\Clink.exe").Mode, "a longer sibling name is not it");
        // trailing separator on the known folder
        Check.Equal(AppMode.Installed, AppLayout.Resolve(Path.Combine(dir, "Clink.exe"), false, Local + @"\").Mode);
    }

    [UnitTest]
    public static void Layout_every_mode_uses_todays_updates_dir()
    {
        string today = Path.Combine(Local, "Clink", "updates");
        Check.Equal(today, At(@"D:\Stick\Clink.exe").UpdatesDir);
        Check.Equal(today, At(Path.Combine(Local, "Programs", "Clink", "Clink.exe")).UpdatesDir);
        Check.Equal(today, At(@"D:\Stick\Clink.exe", dev: true).UpdatesDir);
    }

    [UnitTest]
    public static void Layout_without_local_app_data_never_uses_the_current_directory()
    {
        // An empty %LOCALAPPDATA% must not turn "Programs\Clink" / "Clink\updates" into paths under the current directory.
        string cwdInstalled = Path.Combine(Directory.GetCurrentDirectory(), "Programs", "Clink", "Clink.exe");
        foreach (string? local in new[] { "", "   ", null, @"relative\Local", @"\NoDrive\Local", "C:NoSlash" })
        {
            var l = AppLayout.Resolve(cwdInstalled, false, local);
            Check.Equal(AppMode.Standalone, l.Mode, $"'{local}': not installed");
            Check.Null(l.UpdatesDir, $"'{local}': no updates folder");
            Check.Equal(Path.GetDirectoryName(cwdInstalled), l.ExeDir, $"'{local}'");
            Check.Null(AppLayout.Resolve(cwdInstalled, true, local).UpdatesDir, $"'{local}' (dev)");
        }
    }

    [UnitTest]
    public static void Layout_non_portable_settings_json_is_unchanged()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var s = new Settings { ClipsFolder = Path.Combine(dir.Path, "Clips"), ScreenshotsFolder = Path.Combine(dir.Path, "Shots") };
        s.Save();
        string saved = File.ReadAllText(Settings.FilePath);
        Check.Equal(s.ToJson(), saved, "the same text a plain serialize gives");
        Check.Contains(saved, System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir.Path, "Clips")));
        // The signed feed's floors are left out while unset, so the file has exactly the keys it had before the feed (dev b8b3dc3).
        Check.DoesNotContain(saved, "FeedFloor");
        var back = Settings.Load();
        Check.Equal(Path.Combine(dir.Path, "Clips"), back.ClipsFolder);
        Check.Equal(Path.Combine(dir.Path, "Shots"), back.ScreenshotsFolder);
    }
}
