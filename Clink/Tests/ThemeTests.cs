using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>Themes: every theme and accent has every token, unknown ids fall back to Dark / Spark Blue, the text stays readable (WCAG contrast), Palette.xaml matches the code.</summary>
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
        Check.Equal(new[] { "spark", "blue", "violet", "pink", "orange", "green" }, ThemeManager.Accents.Select(a => a.Id).ToArray());
        Check.Equal("dark", SettingsDefaults.Theme, "a fresh install is Dark");
        Check.Equal("spark", SettingsDefaults.Accent, "a fresh install is Spark Blue");
        Check.True(ThemeManager.Themes.Concat(ThemeManager.Accents).All(c => c.Label.Length > 0 && c.Id == c.Id.ToLowerInvariant()), "labels to show, lower-case ids to store");
        Check.Equal("Light", ThemeManager.Themes[1].Label);
        Check.Equal("Violet", ThemeManager.AccentLabel("violet"));
        Check.Equal("Spark Blue", ThemeManager.AccentLabel("no such accent"));
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
    public static void UnknownIdsFallBackToDarkSpark()
    {
        var darkSpark = ThemeManager.Tokens("dark", "spark");
        foreach (string? bad in new string?[] { null, "", "  ", "neon", "darkest", "cyan!", "spark!" })
        {
            Check.Equal("dark", ThemeManager.NormalizeTheme(bad), $"theme '{bad}'");
            Check.Equal("spark", ThemeManager.NormalizeAccent(bad), $"accent '{bad}'");
            Check.Equal(darkSpark, ThemeManager.Tokens(bad, bad), $"both '{bad}'");
        }
        // one unknown id only replaces its own half
        Check.Equal(ThemeManager.Tokens("light", "spark"), ThemeManager.Tokens("light", "neon"));
        Check.Equal(ThemeManager.Tokens("dark", "violet"), ThemeManager.Tokens("neon", "violet"));
        // hand-edited settings.json: case and spaces do not matter
        Check.Equal("light", ThemeManager.NormalizeTheme(" LIGHT "));
        Check.Equal("blue", ThemeManager.NormalizeAccent("Blue"));
        Check.Equal(ThemeManager.Tokens("light", "pink"), ThemeManager.Tokens("Light", " PINK "));
    }

    [UnitTest]
    public static void DarkSparkIsTheBrandPalette()
    {
        // Deep, Charcoal, Spark Blue and Light from the Euphoric Software brand
        var t = ThemeManager.Tokens("dark", "spark");
        Check.Equal(RgbColor(0x1B1D24), t["Bg"], "Deep");
        Check.Equal(RgbColor(0x272932), t["Panel"], "Charcoal");
        Check.Equal(RgbColor(0x363946), t["Border"], "Border");
        Check.Equal(RgbColor(0x6F9CC4), t["Accent"], "Spark Blue");
        Check.Equal(RgbColor(0x8EB1D2), t["Accent2"], "pale Spark Blue");
        Check.Equal(RgbColor(0xE9EBF2), t["Fg"], "Light");
        Check.Equal(RgbColor(0x9EA4B3), t["Muted"], "Muted");
        Check.Equal(RgbColor(0x1B1D24), t["OnAccent"], "dark text on the accent fill");
        Check.Equal(RgbColor(0x3B82F6), ThemeManager.Tokens("dark", "blue")["Accent"], "the Blue accent is unchanged");
        Check.Equal(RgbColor(0x44739C), ThemeManager.Tokens("light", "spark")["Accent"], "Spark Blue on Light");
        Check.Equal(RgbColor(0x365F85), ThemeManager.Tokens("light", "spark")["Accent2"]);
        Check.Equal(RgbColor(0xF4F5F8), ThemeManager.Tokens("light", "spark")["Bg"]);
        Check.Equal(RgbColor(0x272932), ThemeManager.Tokens("light", "spark")["Fg"]);
    }

    [UnitTest]
    public static void LegacyCyanIsSparkBlue()
    {
        Check.Equal("spark", ThemeManager.NormalizeAccent("cyan"));
        Check.Equal("spark", ThemeManager.NormalizeAccent(" CYAN "));
        Check.False(ThemeManager.Accents.Any(a => a.Id == "cyan"), "cyan is not offered any more");
        Check.Equal("Spark Blue", ThemeManager.AccentLabel("cyan"));
        foreach (var theme in ThemeManager.Themes)
            Check.Equal(ThemeManager.Tokens(theme.Id, "spark"), ThemeManager.Tokens(theme.Id, "cyan"), theme.Id + ": cyan paints as spark");
        Check.Equal("spark", ThemeManager.UpgradeAccent("Cyan"));
        Check.Equal("violet", ThemeManager.UpgradeAccent("violet"));
        Check.Equal("neon", ThemeManager.UpgradeAccent("neon"));
        Check.Equal(null, ThemeManager.UpgradeAccent(null));
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
        Check.NotEqual(ThemeManager.AccentColor("spark", "dark"), ThemeManager.AccentColor("spark", "light"), "a swatch shows its colour in the theme it is on");
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
            // Accent2 is link and status text: 4.5:1 on the window and on cards
            foreach (string panel in new[] { "Bg", "Panel" })
            {
                double ratio = Contrast(t["Accent2"], t[panel]);
                Check.True(ratio >= 4.5, $"{theme}/{accent}: Accent2 on {panel} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs 4.5:1 (Accent2 is link and status text)");
            }
        }
    }

    [UnitTest]
    public static void TheLibrarySecondarySwitchIsReadableInEveryTheme()
    {
        // SegmentSecondary (Theme.xaml) + its track (ClipsPane.xaml): Muted text on a Panel track, Fg on the chosen Panel3 segment.
        foreach (var (theme, accent) in AllCombinations())
        {
            var t = ThemeManager.Tokens(theme, accent);
            double muted = Contrast(t["Muted"], t["Panel"]);
            double chosen = Contrast(t["Fg"], t["Panel3"]);
            Check.True(muted >= 4.5, $"{theme}/{accent}: Muted on the Panel track is {muted.ToString("0.00", CultureInfo.InvariantCulture)}:1");
            Check.True(chosen >= 4.5, $"{theme}/{accent}: Fg on the chosen segment is {chosen.ToString("0.00", CultureInfo.InvariantCulture)}:1");
        }
    }

    [UnitTest]
    public static void TheSelectedRowBorderIsVisibleInEveryTheme()
    {
        // ClipsPane.xaml LibRow: a selected row is a Panel2 fill with an Accent2 border, on the window (Bg) or a card; the border must stand out from both (WCAG 1.4.11, 3:1).
        foreach (var (theme, accent) in AllCombinations())
        {
            var t = ThemeManager.Tokens(theme, accent);
            foreach (string behind in new[] { "Panel2", "Bg" })
            {
                double ratio = Contrast(t["Accent2"], t[behind]);
                Check.True(ratio >= 3.0, $"{theme}/{accent}: selected-row border Accent2 on {behind} is {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1, needs 3:1");
            }
        }
    }
    [UnitTest]
    public static void TheFilterChipIsReadableOffHoveredAndOn()
    {
        // FilterChip (Theme.xaml): off = Muted on the card, hovered = Fg on Panel3 (the hover trigger sets Foreground), on = OnAccent on Accent.
        var app = RunningApp();
        var style = (Style)app.Resources["FilterChip"];
        var template = (System.Windows.Controls.ControlTemplate)style.Setters.OfType<Setter>().Single(s => s.Property == System.Windows.Controls.Control.TemplateProperty).Value;
        var hover = template.Triggers.OfType<Trigger>().Single(t => t.Property == UIElement.IsMouseOverProperty);
        var fg = hover.Setters.OfType<Setter>().SingleOrDefault(s => s.TargetName == null && s.Property == System.Windows.Controls.Control.ForegroundProperty);
        Check.NotNull(fg, "the hover trigger sets the chip's Foreground (Muted on Panel3 is not readable in every theme)");
        Check.True(fg!.Value is DynamicResourceExtension { ResourceKey: "Fg" }, "and it is Fg");
        foreach (var (theme, accent) in AllCombinations())
        {
            var t = ThemeManager.Tokens(theme, accent);
            double off = Contrast(t["Muted"], t["Panel"]);
            double hovered = Contrast(t["Fg"], t["Panel3"]);
            Check.True(off >= 4.5, $"{theme}/{accent}: chip Muted on Panel is {off.ToString("0.00", CultureInfo.InvariantCulture)}:1");
            Check.True(hovered >= 4.5, $"{theme}/{accent}: hovered chip Fg on Panel3 is {hovered.ToString("0.00", CultureInfo.InvariantCulture)}:1");
        }
    }

    [UnitTest]
    public static void The_split_button_styles_have_square_inner_corners_and_a_divider()
    {
        var app = RunningApp();
        var main = new System.Windows.Controls.Button { Style = (Style)app.Resources["AccentSplitMain"] };
        var arrow = new System.Windows.Controls.Button { Style = (Style)app.Resources["AccentSplitArrow"] };
        main.ApplyTemplate();
        arrow.ApplyTemplate();
        var mainBorder = (System.Windows.Controls.Border)main.Template.FindName("B", main);
        var arrowBorder = (System.Windows.Controls.Border)arrow.Template.FindName("B", arrow);
        Check.Equal(new CornerRadius(6, 0, 0, 6), mainBorder.CornerRadius, "Share half: round outside, square inside");
        Check.Equal(new CornerRadius(0, 6, 6, 0), arrowBorder.CornerRadius, "arrow half: round outside, square inside");
        var divider = arrow.Template.FindName("Divider", arrow) as System.Windows.Shapes.Rectangle;
        Check.NotNull(divider, "the arrow half draws the divider");
        Check.Equal(1.0, divider!.Width, "a 1 px divider");
        foreach (var style in new[] { "AccentSplitMain", "AccentSplitArrow" })
        {
            var template = (System.Windows.Controls.ControlTemplate)((Style)app.Resources[style]).Setters.OfType<Setter>().Single(s => s.Property == System.Windows.Controls.Control.TemplateProperty).Value;
            var props = template.Triggers.OfType<Trigger>().Select(t => t.Property).ToList();
            Check.True(props.Contains(UIElement.IsMouseOverProperty) && props.Contains(System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty) && props.Contains(UIElement.IsEnabledProperty),
                style + " has hover, pressed and disabled looks");
            Check.True(((Style)app.Resources[style]).Setters.OfType<Setter>().Any(s => s.Property == FrameworkElement.FocusVisualStyleProperty), style + " has its own keyboard focus ring");
        }
    }

    [UnitTest]
    public static void The_split_divider_is_visible_on_every_accent()
    {
        // the divider is OnAccent at 40 % over the fill: it must differ from the fill on every theme and accent
        foreach (var (theme, accent) in AllCombinations())
        {
            var t = ThemeManager.Tokens(theme, accent);
            Color a = t["Accent"], o = t["OnAccent"];
            Color line = Color.FromRgb((byte)(a.R * 0.6 + o.R * 0.4), (byte)(a.G * 0.6 + o.G * 0.4), (byte)(a.B * 0.6 + o.B * 0.4));
            double divider = Contrast(line, a);
            Check.True(divider >= 1.3, $"{theme}/{accent}: the divider is {divider.ToString("0.00", CultureInfo.InvariantCulture)}:1 against the fill, needs 1.3:1");
        }
    }

    [UnitTest]
    public static void The_text_of_every_accent_button_is_OnAccent_not_the_app_wide_Fg()
    {
        // the app-wide TextBlock style (Foreground Fg) must not reach the text a ContentPresenter generates inside an accent button
        var app = RunningApp();
        var onAccent = ((SolidColorBrush)app.Resources["OnAccent"]).Color;
        var fg = ((SolidColorBrush)app.Resources["Fg"]).Color;
        Check.True(onAccent != fg, "the check needs OnAccent and Fg to differ");
        foreach (string style in new[] { "AccentButton", "AccentSplitMain", "AccentSplitArrow" })
        {
            var button = new System.Windows.Controls.Button { Style = (Style)app.Resources[style], Content = style == "AccentSplitArrow" ? "\u25BE" : "Share" };
            button.Measure(new Size(400, 100));
            button.Arrange(new Rect(0, 0, button.DesiredSize.Width, button.DesiredSize.Height));
            button.UpdateLayout();
            System.Windows.Controls.TextBlock? text = null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(button);
            while (queue.Count > 0 && text == null)
            {
                var d = queue.Dequeue();
                if (d is System.Windows.Controls.TextBlock tb) { text = tb; break; }
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) queue.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
            Check.NotNull(text, style + ": the button generates a TextBlock for string content");
            var brush = text!.Foreground as SolidColorBrush;
            Check.NotNull(brush, style + ": the text has a solid foreground");
            Check.Equal(onAccent, brush!.Color, style + ": the text is OnAccent");
        }
    }

    // ------------------------------------------------------------------ the resources the app carries

    [UnitTest]
    public static void PaletteXamlIsDarkSpark()
    {
        // App.xaml merges UI/Palette.xaml first; until ThemeManager.Apply replaces it, that is the palette. It must say what the code says.
        var xaml = (ResourceDictionary)Application.LoadComponent(new Uri("/Clink;component/ui/palette.xaml", UriKind.Relative));
        var built = ThemeManager.Build("dark", "spark");
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
            Check.Equal("spark", ThemeManager.Accent, "no accent is Spark Blue");
            Check.True(ThemeManager.IsDark);
            Check.Equal(2, raised);
            Check.Equal(ThemeManager.Tokens("dark", "spark")["Panel"], ((SolidColorBrush)app.Resources["Panel"]).Color, "Panel back to Charcoal");
            Check.NotNull(app.Resources["Card"], "Theme.xaml's styles are still there");
        }
        finally
        {
            ThemeManager.ThemeChanged -= Count;
            ThemeManager.Apply(theme0, accent0);
        }
    }
}
