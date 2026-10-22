using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>Themes: every theme and accent has every token, unknown ids fall back to Dark / Cyan, the text stays readable (WCAG contrast), Palette.xaml matches the code.</summary>
internal static class ThemeTests
{
    // ------------------------------------------------------------------ WCAG 2.x contrast of two opaque sRGB colours

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Every theme with every accent, as ids.</summary>
    private static List<(string Theme, string Accent)> AllCombinations() =>
        ThemeManager.Themes.SelectMany(t => ThemeManager.Accents.Select(a => (t.Id, a.Id))).ToList();

    private static Color RgbColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ------------------------------------------------------------------ the lists

    [UnitTest]
    public static void IdsAreTheOnesSettingsDocuments()
    {
        Check.Equal(new[] { "dark", "light" }, ThemeManager.Themes.Select(t => t.Id).ToArray());
        Check.Equal(new[] { "cyan", "blue", "violet", "pink", "orange", "green" }, ThemeManager.Accents.Select(a => a.Id).ToArray());
        Check.Equal("dark", SettingsDefaults.Theme, "a fresh install is Dark");
        Check.Equal("cyan", SettingsDefaults.Accent, "a fresh install is Cyan");
        Check.True(ThemeManager.Themes.Concat(ThemeManager.Accents).All(c => c.Label.Length > 0 && c.Id == c.Id.ToLowerInvariant()), "labels to show, lower-case ids to store");
        Check.Equal("Light", ThemeManager.Themes[1].Label);
        Check.Equal("Violet", ThemeManager.AccentLabel("violet"));
        Check.Equal("Cyan", ThemeManager.AccentLabel("no such accent"));
    }

    // ------------------------------------------------------------------ tokens

    [UnitTest]
    public static void EveryThemeAndAccentHasEveryToken()
    {
        Check.Equal(16, ThemeManager.TokenNames.Count);
        foreach (var (theme, accent) in AllCombinations())
        {
            string which = $"{theme}/{accent}";
            var tokens = ThemeManager.Tokens(theme, accent);
            Check.Equal(ThemeManager.TokenNames.Count, tokens.Count, which + ": token count");
            var dict = ThemeManager.Build(theme, accent);
            Check.True(dict["BgColor"] is Color bgColor && bgColor == tokens["Bg"], which + ": BgColor is the colour of Bg");
            foreach (string name in ThemeManager.TokenNames)
            {
                Check.True(tokens.ContainsKey(name), $"{which}: {name} in Tokens");
                Check.True(dict[name] is SolidColorBrush, $"{which}: {name} is a brush");
                var brush = (SolidColorBrush)dict[name]!;
                Check.True(brush.IsFrozen, $"{which}: {name} is frozen");
                Check.Equal(tokens[name], brush.Color, $"{which}: {name} colour");
                double opacity = name == "WarnTint" ? ThemeManager.WarnTintOpacity : name == "DangerTint" ? ThemeManager.DangerTintOpacity : 1;
                Check.Near(opacity, brush.Opacity, 1e-9, $"{which}: {name} opacity");
            }
            Check.Equal(tokens["Warn"], tokens["WarnTint"], which + ": WarnTint is Warn");
            Check.Equal(tokens["Danger"], tokens["DangerTint"], which + ": DangerTint is Danger");
            Check.Near(0.12, ThemeManager.WarnTintOpacity, 1e-9);
            Check.Near(0.44, ThemeManager.DangerTintOpacity, 1e-9);
        }
    }

    [UnitTest]
    public static void UnknownIdsFallBackToDarkCyan()
    {
        var darkCyan = ThemeManager.Tokens("dark", "cyan");
        foreach (string? bad in new string?[] { null, "", "  ", "neon", "darkest", "cyan!" })
        {
            Check.Equal("dark", ThemeManager.NormalizeTheme(bad), $"theme '{bad}'");
            Check.Equal("cyan", ThemeManager.NormalizeAccent(bad), $"accent '{bad}'");
            Check.Equal(darkCyan, ThemeManager.Tokens(bad, bad), $"both '{bad}'");
        }
        // one unknown id only replaces its own half
        Check.Equal(ThemeManager.Tokens("light", "cyan"), ThemeManager.Tokens("light", "neon"));
        Check.Equal(ThemeManager.Tokens("dark", "violet"), ThemeManager.Tokens("neon", "violet"));
        // hand-edited settings.json: case and spaces do not matter
        Check.Equal("light", ThemeManager.NormalizeTheme(" LIGHT "));
        Check.Equal("blue", ThemeManager.NormalizeAccent("Blue"));
        Check.Equal(ThemeManager.Tokens("light", "pink"), ThemeManager.Tokens("Light", " PINK "));
    }

    [UnitTest]
    public static void DarkCyanIsTheBrandPalette()
    {
        // Midnight, Slate, Electric Cyan, Blue Cyan and Cool White from the brand board
        var t = ThemeManager.Tokens("dark", "cyan");
        Check.Equal(RgbColor(0x0A0E14), t["Bg"], "Midnight");
        Check.Equal(RgbColor(0x1A1F2B), t["Panel"], "Slate");
        Check.Equal(RgbColor(0x22D3EE), t["Accent"], "Electric Cyan");
        Check.Equal(RgbColor(0x3B82F6), t["Accent2"], "Blue Cyan");
        Check.Equal(RgbColor(0xEAF2FF), t["Fg"], "Cool White");
        Check.Equal(RgbColor(0x0A0E14), t["OnAccent"], "dark text on the accent fill");
        Check.Equal(RgbColor(0x3B82F6), ThemeManager.Tokens("dark", "blue")["Accent"], "the Blue accent is Blue Cyan");
        Check.Equal(RgbColor(0x0891B2), ThemeManager.Tokens("light", "cyan")["Accent"], "Cyan on Light");
        Check.Equal(RgbColor(0xF4F6FA), ThemeManager.Tokens("light", "cyan")["Bg"]);
        Check.Equal(RgbColor(0x0A0E14), ThemeManager.Tokens("light", "cyan")["Fg"]);
    }

    [UnitTest]
    public static void EachAccentLooksDifferent()
    {
        foreach (var theme in ThemeManager.Themes)
        {
            var colours = ThemeManager.Accents.Select(a => ThemeManager.AccentColor(a.Id, theme.Id)).ToList();
            Check.Equal(colours.Count, colours.Distinct().Count(), theme.Id + ": six different accents");
            var dims = ThemeManager.Accents.Select(a => ThemeManager.Tokens(theme.Id, a.Id)["AccentDim"]).ToList();
            Check.Equal(dims.Count, dims.Distinct().Count(), theme.Id + ": six different quiet variants");
            foreach (var accent in ThemeManager.Accents)
                Check.NotEqual(ThemeManager.Tokens(theme.Id, accent.Id)["Accent"], ThemeManager.Tokens(theme.Id, accent.Id)["AccentDim"], $"{theme.Id}/{accent.Id}: AccentDim differs from Accent");
        }
        Check.NotEqual(ThemeManager.AccentColor("cyan", "dark"), ThemeManager.AccentColor("cyan", "light"), "a swatch shows its colour in the theme it is on");
    }

    // ------------------------------------------------------------------ contrast

    [UnitTest]
    public static void TextOnTheAccentFillIsReadable()
    {
        foreach (var (theme, accent) in AllCombinations())
        {
            var t = ThemeManager.Tokens(theme, accent);
            double ratio = Contrast(t["OnAccent"], t["Accent"]);
            Check.True(ratio >= 4.5, $"{theme}/{accent}: OnAccent {Hex(t["OnAccent"])} on Accent {Hex(t["Accent"])} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs 4.5:1");
        }
    }

    [UnitTest]
    public static void TextOnThePanelsIsReadable()
    {
        foreach (var (theme, accent) in AllCombinations())
        {
            var t = ThemeManager.Tokens(theme, accent);
            foreach (string panel in new[] { "Bg", "Panel", "Panel2", "Panel3" })
            {
                double fg = Contrast(t["Fg"], t[panel]);
                Check.True(fg >= 4.5, $"{theme}/{accent}: Fg on {panel} is {fg.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs 4.5:1");
            }
            // secondary text: every surface it sits on (Panel3 is hover and track only)
            foreach (string panel in new[] { "Bg", "Panel", "Panel2" })
            {
                double muted = Contrast(t["Muted"], t[panel]);
                Check.True(muted >= 4.5, $"{theme}/{accent}: Muted on {panel} is {muted.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs 4.5:1");
            }
            // status colours are used as text on the window and on cards
            foreach (string status in new[] { "Good", "Warn", "Danger" })
                foreach (string panel in new[] { "Bg", "Panel" })
                {
                    double ratio = Contrast(t[status], t[panel]);
                    Check.True(ratio >= 4.5, $"{theme}/{accent}: {status} on {panel} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs 4.5:1");
                }
            // the accent is a fill and a glyph colour: 3:1 against the card it sits on (WCAG 1.4.11); Accent2 (links, focus rings) likewise
            foreach (string colour in new[] { "Accent", "Accent2" })
            {
                double ratio = Contrast(t[colour], t["Panel"]);
                Check.True(ratio >= 3.0, $"{theme}/{accent}: {colour} on Panel is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs 3:1");
            }
        }
    }

    // ------------------------------------------------------------------ the resources the app carries

    [UnitTest]
    public static void PaletteXamlIsDarkCyan()
    {
        // App.xaml merges UI/Palette.xaml first; until ThemeManager.Apply replaces it, that is the palette. It must say what the code says.
        var xaml = (ResourceDictionary)Application.LoadComponent(new Uri("/Clink;component/ui/palette.xaml", UriKind.Relative));
        var built = ThemeManager.Build("dark", "cyan");
        Check.Equal(built["BgColor"], xaml["BgColor"], "BgColor");
        foreach (string name in ThemeManager.TokenNames)
        {
            Check.True(xaml[name] is SolidColorBrush, "Palette.xaml has " + name);
            var a = (SolidColorBrush)xaml[name]!;
            var b = (SolidColorBrush)built[name]!;
            Check.Equal(b.Color, a.Color, name + " colour");
            Check.Near(b.Opacity, a.Opacity, 1e-9, name + " opacity");
        }
        Check.Equal(ThemeManager.TokenNames.Count + 1, xaml.Count, "Palette.xaml holds the tokens and BgColor, nothing else");
    }

    // The tests run inside Clink.exe --unittest, on the UI thread, with App.xaml's resources loaded.
    private static Application RunningApp() => Application.Current ?? throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");

    [UnitTest]
    public static void FontsAreTheBrandFonts()
    {
        var app = RunningApp();
        Check.Equal("Inter, Segoe UI Variable Text, Segoe UI", ((FontFamily)app.Resources["UiFont"]).Source);
        Check.Equal("Sora, Segoe UI Variable Display, Segoe UI", ((FontFamily)app.Resources["DisplayFont"]).Source);
    }

    [UnitTest]
    public static void ApplyPutsThePaletteInPlace()
    {
        var app = RunningApp();
        string theme0 = ThemeManager.Theme, accent0 = ThemeManager.Accent;
        int dictionaries = app.Resources.MergedDictionaries.Count;
        int raised = 0;
        void Count() => raised++;
        ThemeManager.ThemeChanged += Count;
        try
        {
            ThemeManager.Apply("light", "violet");
            Check.Equal("light", ThemeManager.Theme);
            Check.Equal("violet", ThemeManager.Accent);
            Check.False(ThemeManager.IsDark);
            Check.Equal(1, raised, "ThemeChanged once per Apply");
            Check.Equal(dictionaries, app.Resources.MergedDictionaries.Count, "the palette is replaced, not added to");
            var accent = (SolidColorBrush)app.Resources["Accent"];
            Check.Equal(ThemeManager.Tokens("light", "violet")["Accent"], accent.Color, "Accent in the application's resources");
            Check.Equal(ThemeManager.Tokens("light", "violet")["Bg"], ((SolidColorBrush)app.Resources["Bg"]).Color, "Bg in the application's resources");

            ThemeManager.Apply("nonsense", null);
            Check.Equal("dark", ThemeManager.Theme, "an unknown theme is Dark");
            Check.Equal("cyan", ThemeManager.Accent, "no accent is Cyan");
            Check.True(ThemeManager.IsDark);
            Check.Equal(2, raised);
            Check.Equal(ThemeManager.Tokens("dark", "cyan")["Panel"], ((SolidColorBrush)app.Resources["Panel"]).Color, "Panel back to Slate");
            Check.NotNull(app.Resources["Card"], "Theme.xaml's styles are still there");
        }
        finally
        {
            ThemeManager.ThemeChanged -= Count;
            ThemeManager.Apply(theme0, accent0);
        }
    }
}
