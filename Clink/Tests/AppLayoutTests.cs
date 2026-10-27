using System.IO;
using Clink.Services;

namespace Clink.Tests;

// Portable / installed / standalone layout (AppLayout) and the relative folders in a portable settings.json. Pure: temp dirs only.
internal static class AppLayoutTests
{
    private const string Local = @"C:\Users\Test\AppData\Local";
    private const string Videos = @"C:\Users\Test\Videos";
    private static AppLayout At(string exe, bool dev = false, Func<string, bool>? exists = null) =>
        AppLayout.Resolve(exe, dev, Local, Videos, exists ?? (_ => false));

    [UnitTest]
    public static void Layout_standalone_is_todays_behaviour()
    {
        var l = At(@"D:\Tools\Clink.exe");
        Check.Equal(AppMode.Standalone, l.Mode);
        Check.Null(l.DataDir);
        Check.Equal(Path.Combine(Local, "Clink", "updates"), l.UpdatesDir);
        Check.Equal(Path.Combine(Videos, "Clink"), l.DefaultClipsFolder);
        Check.True(l.IsDataDirWritable(_ => throw new InvalidOperationException("must not probe")), "nothing to probe without a data dir");
        Check.Equal(AppMode.Standalone, AppLayout.Resolve(null, false, Local, Videos, _ => false).Mode);
    }

    [UnitTest]
    public static void Layout_portable_is_detected_by_the_marker_beside_the_exe()
    {
        using var dir = new TempDir();
        string exe = dir.File("Clink.exe");
        Check.Equal(AppMode.Standalone, AppLayout.Resolve(exe, false, Local, Videos, File.Exists).Mode, "no marker");
        File.WriteAllText(dir.File("Clink.portable"), "");
        var l = AppLayout.Resolve(exe, false, Local, Videos, File.Exists);
        Check.Equal(AppMode.Portable, l.Mode);
        Check.True(l.IsPortable);
        Check.Equal(Path.Combine(dir.Path, "ClinkData"), l.DataDir);
        Check.Equal(Path.Combine(dir.Path, "ClinkData", "updates"), l.UpdatesDir);
        Check.Equal(Path.Combine(dir.Path, "ClinkData", "Clips"), l.DefaultClipsFolder);
    }

    [UnitTest]
    public static void Layout_marker_must_be_beside_the_exe()
    {
        string seen = "";
        At(@"D:\Tools\Clink.exe", exists: p => { seen = p; return false; });
        Check.Equal(@"D:\Tools\Clink.portable", seen);
    }

    [UnitTest]
    public static void Layout_dev_wins_and_marker_beats_installed()
    {
        string installed = Path.Combine(Local, "Programs", "Clink", "Clink.exe");
        var dev = At(installed, dev: true, exists: _ => true);
        Check.Equal(AppMode.Dev, dev.Mode, "--dev beats the marker");
        Check.Null(dev.DataDir);
        Check.Equal(Path.Combine(Local, "Clink", "updates"), dev.UpdatesDir);
        Check.Equal(AppMode.Portable, At(installed, exists: _ => true).Mode, "marker beats the installed location");
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
        Check.Null(At(Path.Combine(dir, "Clink.exe")).DataDir);
        // trailing separator on the known folder
        Check.Equal(AppMode.Installed, AppLayout.Resolve(Path.Combine(dir, "Clink.exe"), false, Local + @"\", Videos, _ => false).Mode);
    }

    [UnitTest]
    public static void Layout_portable_and_others_use_different_updates_dirs()
    {
        var portable = At(@"D:\Stick\Clink.exe", exists: _ => true);
        var standalone = At(@"D:\Stick\Clink.exe");
        var installed = At(Path.Combine(Local, "Programs", "Clink", "Clink.exe"));
        Check.NotEqual(portable.UpdatesDir, standalone.UpdatesDir);
        Check.Equal(standalone.UpdatesDir, installed.UpdatesDir, "standalone and installed share today's folder");
        Check.Equal(@"D:\Stick\ClinkData\updates", portable.UpdatesDir);
    }

    [UnitTest]
    public static void Layout_data_dir_writable_probe_is_injectable()
    {
        var portable = At(@"D:\Stick\Clink.exe", exists: _ => true);
        string? probed = null;
        Check.True(portable.IsDataDirWritable(d => { probed = d; return true; }));
        Check.Equal(@"D:\Stick\ClinkData", probed);
        Check.False(portable.IsDataDirWritable(_ => false));
        Check.False(portable.IsDataDirWritable(_ => throw new UnauthorizedAccessException()), "a throwing probe means not writable");
        using var dir = new TempDir();
        File.WriteAllText(dir.File("Clink.portable"), "");
        var real = AppLayout.Resolve(dir.File("Clink.exe"), false, Local, Videos, File.Exists);
        Check.True(real.IsDataDirWritable(), "default probe creates the folder and writes into it");
        Check.True(Directory.Exists(real.DataDir!));
    }

    [UnitTest]
    public static void Layout_stored_paths_are_relative_inside_the_root_only()
    {
        const string root = @"E:\App\ClinkData";
        Check.Equal("Clips", AppLayout.ToStoredPath(@"E:\App\ClinkData\Clips", root));
        Check.Equal(@"Shots\2026", AppLayout.ToStoredPath(@"e:\app\clinkdata\Shots\2026\", root + @"\"));
        Check.Equal(".", AppLayout.ToStoredPath(root, root));
        Check.Equal(@"E:\App\ClinkData2\Clips", AppLayout.ToStoredPath(@"E:\App\ClinkData2\Clips", root), "sibling with the same prefix stays absolute");
        Check.Equal(@"D:\Videos\Clink", AppLayout.ToStoredPath(@"D:\Videos\Clink", root), "outside stays absolute");
        Check.Equal(@"E:\App\ClinkData\Clips", AppLayout.ToStoredPath(@"E:\App\ClinkData\Clips", null), "no root: unchanged");
        Check.Null(AppLayout.ToStoredPath(null, root));

        Check.Equal(@"E:\App\ClinkData\Clips", AppLayout.FromStoredPath("Clips", root));
        Check.Equal(root, AppLayout.FromStoredPath(".", root));
        Check.Equal(@"D:\Videos\Clink", AppLayout.FromStoredPath(@"D:\Videos\Clink", root), "absolute stays");
        Check.Equal(@"..\Elsewhere", AppLayout.FromStoredPath(@"..\Elsewhere", root), "climbing out is not resolved");
        Check.Equal("Clips", AppLayout.FromStoredPath("Clips", null));
    }

    [UnitTest]
    public static void Layout_relative_paths_survive_a_drive_letter_change()
    {
        string stored = AppLayout.ToStoredPath(@"E:\App\ClinkData\Clips", @"E:\App\ClinkData")!;
        Check.Equal(@"F:\Portable\ClinkData\Clips", AppLayout.FromStoredPath(stored, @"F:\Portable\ClinkData"));
    }

    [UnitTest]
    public static void Layout_portable_settings_save_relative_and_load_on_another_root()
    {
        using var dir = new TempDir();
        string rootA = dir.File(@"A\ClinkData"), rootB = dir.File(@"B\ClinkData"), outside = dir.File("OutsideShots");
        try
        {
            Settings.PortableRoot = rootA;
            Settings.DirOverride = rootA;
            Settings.DefaultClipsFolderOverride = Path.Combine(rootA, "Clips");
            var fresh = new Settings();
            Check.Equal(Path.Combine(rootA, "Clips"), fresh.ClipsFolder, "portable default clips folder");
            fresh.ScreenshotsFolder = Path.Combine(rootA, "Shots");
            fresh.Save();
            string text = File.ReadAllText(Settings.FilePath);
            Check.Contains(text, "\"ClipsFolder\": \"Clips\"");
            Check.Contains(text, "\"ScreenshotsFolder\": \"Shots\"");
            Check.Equal(Path.Combine(rootA, "Clips"), fresh.ClipsFolder, "saving leaves the live value absolute");

            // Same file, new root (the stick came back as another drive).
            Directory.CreateDirectory(rootB);
            File.WriteAllText(Path.Combine(rootB, "settings.json"), text);
            Settings.PortableRoot = rootB;
            Settings.DirOverride = rootB;
            var moved = Settings.Load();
            Check.Equal(Path.Combine(rootB, "Clips"), moved.ClipsFolder);
            Check.Equal(Path.Combine(rootB, "Shots"), moved.ScreenshotsFolder);

            // Paths outside the root stay absolute in the file and after loading.
            moved.ClipsFolder = outside;
            moved.ScreenshotsFolder = null;
            moved.Save();
            string text2 = File.ReadAllText(Settings.FilePath);
            Check.Contains(text2, System.Text.Json.JsonSerializer.Serialize(outside));
            var again = Settings.Load();
            Check.Equal(outside, again.ClipsFolder);
            Check.Null(again.ScreenshotsFolder);
        }
        finally { Settings.PortableRoot = null; Settings.DefaultClipsFolderOverride = null; }
    }

    [UnitTest]
    public static void Layout_non_portable_settings_json_is_unchanged()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        Check.Null(Settings.PortableRoot);
        var s = new Settings { ClipsFolder = Path.Combine(dir.Path, "Clips"), ScreenshotsFolder = Path.Combine(dir.Path, "Shots") };
        s.Save();
        string saved = File.ReadAllText(Settings.FilePath);
        Check.Equal(s.ToJson(), saved, "the same text a plain serialize gives");
        Check.Contains(saved, System.Text.Json.JsonSerializer.Serialize(Path.Combine(dir.Path, "Clips")));
        var back = Settings.Load();
        Check.Equal(Path.Combine(dir.Path, "Clips"), back.ClipsFolder);
    }
}
