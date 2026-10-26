using System.IO;
using System.Text.RegularExpressions;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The App tab's import banner text, and a guard that the windows follow the theme (no palette token through a StaticResource).</summary>
internal static class AppTabTests
{
    private static ForeignSettings Copy(params string[] differences) => new(
        "Contoso.Notes_pzs8sxrjxfjjc", @"C:\Users\Player\AppData\Local\Packages\Contoso.Notes_pzs8sxrjxfjjc\LocalCache\Roaming\Clink",
        new DateTime(2026, 10, 21, 18, 32, 5, DateTimeKind.Utc), differences);

    [UnitTest]
    public static void The_import_banner_names_the_copy_when_it_was_saved_and_what_would_change()
    {
        var copy = Copy("video quality", "replay length", "hotkeys");
        Check.Equal($"Clink also has settings saved {copy.SavedLocalText} by a copy started from inside Notes (video quality, replay length, hotkeys)",
            MainWindow.ImportBannerText(copy));
    }

    [UnitTest]
    public static void The_import_banner_has_no_brackets_when_nothing_is_listed()
    {
        var copy = Copy();
        Check.Equal($"Clink also has settings saved {copy.SavedLocalText} by a copy started from inside Notes", MainWindow.ImportBannerText(copy));
    }

    // Every colour in a window has to be a DynamicResource: a StaticResource keeps the brush it found when the window loaded, so
    // that part of the window would stay in the old theme after a switch. Theme.xaml, Palette.xaml and every window say so with
    // {DynamicResource Name}; this reads the XAML sources (when the tests run from a checkout's build folder) and fails on one that
    // does not. The other tokens-as-text rule: the old hard-coded text-on-accent colour is gone.
    [UnitTest]
    public static void No_xaml_refers_to_a_palette_token_through_a_StaticResource()
    {
        string? root = FindSourceFolder();
        if (root == null) return;   // not run from a checkout: nothing to read
        var token = new Regex(@"\{StaticResource\s+(Bg|BgColor|Panel|Panel2|Panel3|Border|Fg|Muted|Accent|AccentDim|Accent2|OnAccent|Good|Warn|WarnTint|Danger|DangerTint)\}");
        var problems = new List<string>();
        int files = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar) || rel.StartsWith("obj" + Path.DirectorySeparatorChar) || rel.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            files++;
            string text = File.ReadAllText(file);
            foreach (Match m in token.Matches(text)) problems.Add($"{rel}: {m.Value}");
            if (text.Contains("#FF0E1419", StringComparison.OrdinalIgnoreCase)) problems.Add($"{rel}: the hard-coded text-on-accent colour (use {{DynamicResource OnAccent}})");
        }
        Check.True(files >= 8, $"expected to find the app's XAML files under {root}, found {files}");
        Check.Equal(0, problems.Count, "colours that would not follow a theme change: " + string.Join("; ", problems.Take(8)));
    }

    /// <summary>The folder of Clink.csproj above the running exe (a build folder of a checkout), or null.</summary>
    private static string? FindSourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Clink.csproj"))) return dir.FullName;
        return null;
    }
}
