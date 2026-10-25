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

    // ---- raster tools (S4)
    public const string DefaultHighlightColor = "#FFEB3B", DefaultOutlineColor = "#FFFFFF";
    /// <summary>Blur: the Gaussian sigma in picture pixels.</summary>
    public int BlurRadius { get; set; } = 8;
    /// <summary>Pixelate: the block size in picture pixels.</summary>
    public int PixelateBlock { get; set; } = 12;
    public string HighlightColor { get; set; } = DefaultHighlightColor;
    /// <summary>The pen width of a freehand highlight.</summary>
    public double HighlightWidth { get; set; } = 18;
    /// <summary>Highlight draws freehand (the toolbar's toggle); false = a rectangle (Shift keeps it square).</summary>
    public bool HighlightFreehand { get; set; }
    /// <summary>Spotlight: how dark the outside is (0 = not at all, 0.95 = nearly black).</summary>
    public double SpotDim { get; set; } = 0.55;
    /// <summary>Spotlight: the blur sigma of the outside.</summary>
    public int SpotRadius { get; set; } = 12;
    /// <summary>Spotlight: the blur grows with the distance from the box (false = one blur level).</summary>
    public bool SpotGradient { get; set; }
    /// <summary>Spotlight gradient: the distance over which the blur reaches its full strength, in % of the picture's shorter side.</summary>
    public int SpotFalloffPercent { get; set; } = 35;
    public double MagnifyZoom { get; set; } = 2;
    public bool MagnifyOutline { get; set; } = true;
    public string MagnifyOutlineColor { get; set; } = DefaultOutlineColor;
    public double MagnifyOutlineWidth { get; set; } = 3;
    /// <summary>Magnify: bilinear instead of crisp pixels.</summary>
    public bool MagnifySmooth { get; set; }
    /// <summary>An inserted image's opacity (0.1 .. 1).</summary>
    public double ImageOpacity { get; set; } = 1;
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
        HighlightColor = NormalColor(HighlightColor, DefaultHighlightColor);
        MagnifyOutlineColor = NormalColor(MagnifyOutlineColor, DefaultOutlineColor);
        BlurRadius = Math.Clamp(BlurRadius, 1, 40);
        PixelateBlock = Math.Clamp(PixelateBlock, 2, 64);
        HighlightWidth = double.IsFinite(HighlightWidth) ? Math.Clamp(HighlightWidth, 2, 80) : 18;
        SpotDim = double.IsFinite(SpotDim) ? Math.Clamp(SpotDim, 0, 0.95) : 0.55;
        SpotRadius = Math.Clamp(SpotRadius, 0, 40);
        SpotFalloffPercent = Math.Clamp(SpotFalloffPercent, 5, 100);
        MagnifyZoom = double.IsFinite(MagnifyZoom) ? Math.Clamp(MagnifyZoom, 1.25, 8) : 2;
        MagnifyOutlineWidth = double.IsFinite(MagnifyOutlineWidth) ? Math.Clamp(MagnifyOutlineWidth, 1, 12) : 3;
        ImageOpacity = double.IsFinite(ImageOpacity) ? Math.Clamp(ImageOpacity, 0.1, 1) : 1;
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
    internal System.Windows.Media.Color HighlightColorValue => TryParseColor(HighlightColor, out var c) ? c : System.Windows.Media.Color.FromRgb(0xFF, 0xEB, 0x3B);
    internal System.Windows.Media.Color MagnifyOutlineColorValue => TryParseColor(MagnifyOutlineColor, out var c) ? c : System.Windows.Media.Colors.White;
    internal System.Windows.Media.Color BackdropColorValue => TryParseColor(BackdropColor, out var c) ? c : System.Windows.Media.Colors.Black;

    public ScreenshotEditorOptions Clone()
    {
        var c = (ScreenshotEditorOptions)MemberwiseClone();
        c.Unknown = Unknown == null ? null : new Dictionary<string, JsonElement>(Unknown);
        return c;
    }
}

