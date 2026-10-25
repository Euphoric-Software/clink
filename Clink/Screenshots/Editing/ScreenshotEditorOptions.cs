using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clink.Screenshots.Editing;

/// <summary>
/// The editor's last-used tool options, kept in <c>Settings.ScreenshotEditor</c> so the next editor opens the way the last one was
/// left (colour, line width, text size and backdrop). Colours are "#RRGGBB" / "#AARRGGBB" text so the settings file stays readable
/// and a hand edit that makes no sense is just put back by <see cref="Normalize"/>. Later slices add their fields (blur radius,
/// highlight colour...) here; keys this version does not know are kept.
/// </summary>
public sealed class ScreenshotEditorOptions
{
    public const string DefaultColor = "#E53935", DefaultTextColor = "#FFFFFF", DefaultBackdropColor = "#000000";
    public const double MinWidth = 1, MaxWidth = 40, MinFontSize = 8, MaxFontSize = 200;

    /// <summary>The colour of pen / line / arrow / rectangle.</summary>
    public string Color { get; set; } = DefaultColor;
    public double Width { get; set; } = 4;
    public string TextColor { get; set; } = DefaultTextColor;
    public double FontSize { get; set; } = 28;
    public bool Bold { get; set; }
    /// <summary>none | box | outline</summary>
    public string TextBackdrop { get; set; } = "outline";
    public string BackdropColor { get; set; } = DefaultBackdropColor;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>Puts every value back into its range (or its default when it is not a colour / a known word / a finite number).</summary>
    public void Normalize()
    {
        Color = NormalColor(Color, DefaultColor);
        TextColor = NormalColor(TextColor, DefaultTextColor);
        BackdropColor = NormalColor(BackdropColor, DefaultBackdropColor);
        Width = double.IsFinite(Width) ? Math.Clamp(Width, MinWidth, MaxWidth) : 4;
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, MinFontSize, MaxFontSize) : 28;
        string b = (TextBackdrop ?? "").Trim().ToLowerInvariant();
        TextBackdrop = b is "none" or "box" or "outline" ? b : "outline";
    }

    private static string NormalColor(string? text, string fallback) => TryParseColor(text, out var c) ? ToHex(c) : fallback;

    /// <summary>"#RRGGBB" or "#AARRGGBB"; false for anything else.</summary>
    internal static bool TryParseColor(string? text, out System.Windows.Media.Color color)
    {
        color = default;
        string t = (text ?? "").Trim();
        if (t.Length is not (7 or 9) || t[0] != '#') return false;
        if (!uint.TryParse(t.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;
        color = t.Length == 7
            ? System.Windows.Media.Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : System.Windows.Media.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    internal static string ToHex(System.Windows.Media.Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    internal System.Windows.Media.Color ColorValue => TryParseColor(Color, out var c) ? c : System.Windows.Media.Colors.Red;
    internal System.Windows.Media.Color TextColorValue => TryParseColor(TextColor, out var c) ? c : System.Windows.Media.Colors.White;
    internal System.Windows.Media.Color BackdropColorValue => TryParseColor(BackdropColor, out var c) ? c : System.Windows.Media.Colors.Black;

    public ScreenshotEditorOptions Clone()
    {
        var c = (ScreenshotEditorOptions)MemberwiseClone();
        c.Unknown = Unknown == null ? null : new Dictionary<string, JsonElement>(Unknown);
        return c;
    }
}
