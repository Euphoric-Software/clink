using System.Windows;
using System.Windows.Media;
using Clink.Services;

namespace Clink.UI;

/// <summary>A theme or an accent as settings.json names it (<see cref="Id"/>, what is stored) and the App tab shows it (<see cref="Label"/>).</summary>
internal sealed record ThemeChoice(string Id, string Label);

/// <summary>
/// The colour tokens of the app and how a theme (Dark or Light) and an accent (Spark Blue, Blue, Violet, Pink, Orange, Green) become them.
/// <see cref="Apply"/> builds a dictionary of frozen brushes, one per token plus <c>BgColor</c>, and puts it in place of the first
/// merged dictionary of the application's resources, which App.xaml fills with UI/Palette.xaml (the default, Dark with Spark Blue). Every
/// reference to a token that has to follow a switch is a DynamicResource, so open windows repaint at once; a StaticResource keeps
/// the brush it found when it loaded.
///
/// The tokens: Bg (window), Panel (cards, the tab strip, toasts), Panel2 (inputs, buttons, combos), Panel3 (hover, slider track,
/// scrollbar), Border, Fg (text), Muted (secondary text), Accent / AccentDim / OnAccent (the accent set: the fill, its quiet
/// variant, and text on the fill), Accent2 (secondary accent: links, focus rings, the played part of a seek bar, the hover outline
/// of a secondary button), Good / Warn / Danger (status), WarnTint / DangerTint (Warn and Danger at 12 % and 44 % alpha).
/// The Dark values are the brand palette (Deep, Charcoal, Spark Blue, Light).
/// </summary>
internal static class ThemeManager
{
    /// <summary>Dark first. These are the ids a <c>Settings.Theme</c> can hold.</summary>
    public static IReadOnlyList<ThemeChoice> Themes { get; } = new ThemeChoice[] { new("dark", "Dark"), new("light", "Light") };

    /// <summary>Spark Blue first (the default). These are the ids a <c>Settings.Accent</c> can hold.</summary>
    public static IReadOnlyList<ThemeChoice> Accents { get; } = new ThemeChoice[]
    {
        new("spark", "Spark Blue"), new("blue", "Blue"), new("violet", "Violet"), new("pink", "Pink"), new("orange", "Orange"), new("green", "Green"),
    };

    /// <summary>Accent ids an earlier Clink stored that now mean another one: "cyan", the default before the Euphoric Software rebrand, is Spark Blue.</summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyAccents =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["cyan"] = "spark" };

    /// <summary>The id a legacy accent id now means (case and surrounding spaces ignored); any other value as it was, unknown ids included.</summary>
    public static string? UpgradeAccent(string? id) => id != null && LegacyAccents.TryGetValue(id.Trim(), out var now) ? now : id;

    /// <summary>The brush keys <see cref="Build"/> makes, in this order (the dictionary also holds <c>BgColor</c>, a Color).</summary>
    public static IReadOnlyList<string> TokenNames { get; } = new[]
    {
        "Bg", "Panel", "Panel2", "Panel3", "Border", "Fg", "Muted", "Accent", "AccentDim", "Accent2", "OnAccent",
        "Good", "Warn", "WarnTint", "Danger", "DangerTint",
    };

    /// <summary>WarnTint is Warn at this opacity: the background of a warning banner.</summary>
    public const double WarnTintOpacity = 0.12;
    /// <summary>DangerTint is Danger at this opacity: the part of the replay-length slider the machine's RAM cannot hold.</summary>
    public const double DangerTintOpacity = 0.44;

    /// <summary>The theme id in force (what <see cref="Apply"/> last applied; Dark before the first call).</summary>
    public static string Theme { get; private set; } = SettingsDefaults.Theme;
    /// <summary>The accent id in force.</summary>
    public static string Accent { get; private set; } = SettingsDefaults.Accent;
    public static bool IsDark => Theme == "dark";

    /// <summary>Raised on the UI thread after <see cref="Apply"/> has put a new palette in place (owner-drawn controls repaint on it).</summary>
    public static event Action? ThemeChanged;

    /// <summary>The known theme id that <paramref name="id"/> names (case and surrounding spaces ignored), else Dark.</summary>
    public static string NormalizeTheme(string? id) => Match(Themes, id) ?? SettingsDefaults.Theme;

    /// <summary>The known accent id that <paramref name="id"/> names (case and surrounding spaces ignored), else Spark Blue.</summary>
    public static string NormalizeAccent(string? id) => Match(Accents, UpgradeAccent(id)) ?? SettingsDefaults.Accent;

    /// <summary>The label of the accent an id names ("Spark Blue"); Spark Blue's for an id that is not one.</summary>
    public static string AccentLabel(string? id) => Accents.First(a => a.Id == NormalizeAccent(id)).Label;

    /// <summary>The colour of an accent in a theme (the one in force when <paramref name="theme"/> is null): what its swatch shows.</summary>
    public static Color AccentColor(string? accent, string? theme = null) => Tokens(theme ?? Theme, accent)["Accent"];

    /// <summary>
    /// Every token of a theme with an accent, by name (see <see cref="TokenNames"/>). Unknown ids fall back to Dark and Spark Blue.
    /// WarnTint and DangerTint hold the colour of Warn and Danger; <see cref="WarnTintOpacity"/> / <see cref="DangerTintOpacity"/> make them tints.
    /// </summary>
    public static IReadOnlyDictionary<string, Color> Tokens(string? theme, string? accent)
    {
        bool dark = NormalizeTheme(theme) == "dark";
        var p = dark ? DarkBase : LightBase;
        var a = (dark ? DarkAccents : LightAccents)[NormalizeAccent(accent)];
        return new Dictionary<string, Color>
        {
            ["Bg"] = p.Bg, ["Panel"] = p.Panel, ["Panel2"] = p.Panel2, ["Panel3"] = p.Panel3, ["Border"] = p.Border,
            ["Fg"] = p.Fg, ["Muted"] = p.Muted,
            ["Accent"] = a.Accent, ["AccentDim"] = a.AccentDim, ["Accent2"] = p.Accent2, ["OnAccent"] = a.OnAccent,
            ["Good"] = p.Good, ["Warn"] = p.Warn, ["WarnTint"] = p.Warn, ["Danger"] = p.Danger, ["DangerTint"] = p.Danger,
        };
    }

    /// <summary>A dictionary of frozen brushes, one per token, plus <c>BgColor</c> (the colour of Bg). Unknown ids fall back to Dark and Spark Blue.</summary>
    public static ResourceDictionary Build(string? theme, string? accent)
    {
        var tokens = Tokens(theme, accent);
        var dict = new ResourceDictionary { ["BgColor"] = tokens["Bg"] };
        foreach (string name in TokenNames)
        {
            var brush = new SolidColorBrush(tokens[name]);
            if (name == "WarnTint") brush.Opacity = WarnTintOpacity;
            else if (name == "DangerTint") brush.Opacity = DangerTintOpacity;
            brush.Freeze();
            dict[name] = brush;
        }
        return dict;
    }

    /// <summary>
    /// Puts the palette of a theme and an accent in place of the application's first merged dictionary and raises
    /// <see cref="ThemeChanged"/>. Unknown ids mean Dark / Spark Blue. Runs on the UI thread (a call from another thread is handed over to
    /// it). Called once at start, before any window exists, and again when the App tab changes a choice.
    /// </summary>
    public static void Apply(string? theme, string? accent)
    {
        string t = NormalizeTheme(theme), a = NormalizeAccent(accent);
        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(t, a));
            return;
        }
        try
        {
            var palette = Build(t, a);
            if (app != null)
            {
                var merged = app.Resources.MergedDictionaries;
                if (merged.Count == 0) merged.Add(palette);
                else merged[0] = palette;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"theme: could not apply {t}/{a}", ex);   // the palette that was in place stays
            return;
        }
        Theme = t;
        Accent = a;
        Log.Info($"theme: {t}, {a} accent");
        var handlers = ThemeChanged;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { Log.Error("theme: a ThemeChanged handler failed", ex); }
        }
    }

    private static string? Match(IReadOnlyList<ThemeChoice> choices, string? id)
    {
        string wanted = id?.Trim() ?? "";
        return choices.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    // ------------------------------------------------------------------ the token values

    // Everything that does not depend on the accent. Dark is the brand palette: Deep / Charcoal / Light, and a pale Spark Blue as the
    // secondary accent. Panel2, Panel3 and Border are lighter steps of Charcoal. Muted is at least 4.5:1 on every panel but Panel3
    // (hover only). Light keeps the plan's neutral steps; Good, Warn and Danger are darker there so they read as text on white.
    private sealed record BaseColors(
        Color Bg, Color Panel, Color Panel2, Color Panel3, Color Border, Color Fg, Color Muted,
        Color Accent2, Color Good, Color Warn, Color Danger);

    // What the accent chooses: the fill, its quiet variant (a selection range, an ideal zone) and the text on the fill.
    private sealed record AccentColors(Color Accent, Color AccentDim, Color OnAccent);

    private static Color C(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static readonly BaseColors DarkBase = new(
        Bg: C(0x1B1D24), Panel: C(0x272932), Panel2: C(0x30333E), Panel3: C(0x3B3E4B), Border: C(0x363946),
        Fg: C(0xE9EBF2), Muted: C(0x9EA4B3),
        Accent2: C(0x8EB1D2), Good: C(0x5EDB8A), Warn: C(0xF2C94C), Danger: C(0xF26B6B));

    private static readonly BaseColors LightBase = new(
        Bg: C(0xF4F5F8), Panel: C(0xFFFFFF), Panel2: C(0xEEF0F4), Panel3: C(0xE2E5EC), Border: C(0xD6DBE4),
        Fg: C(0x272932), Muted: C(0x5B6373),
        Accent2: C(0x365F85), Good: C(0x15803D), Warn: C(0xA16207), Danger: C(0xC73A3A));

    // Accent / AccentDim / OnAccent. On Dark the text on the fill is Deep everywhere. On Light it is white where white reads
    // (4.5:1 or better) and Deep on Orange, whose fill is too bright for white.
    private static readonly IReadOnlyDictionary<string, AccentColors> DarkAccents = new Dictionary<string, AccentColors>
    {
        ["spark"] = new(C(0x6F9CC4), C(0x3D5A78), C(0x1B1D24)),
        ["blue"] = new(C(0x3B82F6), C(0x2F5D9E), C(0x1B1D24)),
        ["violet"] = new(C(0xB49CFF), C(0x5E4B9E), C(0x1B1D24)),
        ["pink"] = new(C(0xF78FC6), C(0x9E4A78), C(0x1B1D24)),
        ["orange"] = new(C(0xFFB15E), C(0x9E6A2F), C(0x1B1D24)),
        ["green"] = new(C(0x6EE7A0), C(0x2F8F5B), C(0x1B1D24)),
    };

    private static readonly IReadOnlyDictionary<string, AccentColors> LightAccents = new Dictionary<string, AccentColors>
    {
        ["spark"] = new(C(0x44739C), C(0xC5D7E7), C(0xFFFFFF)),
        ["blue"] = new(C(0x2563EB), C(0xC9DAFB), C(0xFFFFFF)),
        ["violet"] = new(C(0x7C3AED), C(0xDDD1FB), C(0xFFFFFF)),
        ["pink"] = new(C(0xDB2777), C(0xF8CFE3), C(0xFFFFFF)),
        ["orange"] = new(C(0xD97706), C(0xF8DDBA), C(0x1B1D24)),
        ["green"] = new(C(0x15803D), C(0xC6EBD3), C(0xFFFFFF)),
    };
}
