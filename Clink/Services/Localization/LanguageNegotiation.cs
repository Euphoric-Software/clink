using System.Text.RegularExpressions;
using Windows.Win32;

namespace Clink.Services.Localization;

/// <summary>
/// Which UI language to use: the Settings.Language choice, else the best match for the Windows display-language list. Mirrors
/// Copium's matchLanguage (web/src/i18n.js): exact match, Traditional Chinese is never answered with Simplified, otherwise the
/// first language of the same base ("zh-Hans", "zh-SG" and "zh" give zh-CN, "pt-PT" gives pt-BR).
/// </summary>
internal static class LanguageNegotiation
{
    public const string Auto = "auto";
    public const string Fallback = "en";

    private static readonly Regex s_tag = new(@"^[a-z]{2,3}(-[a-z0-9]{1,8})*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_traditional = new(@"^zh-(hant|tw|hk|mo)(-|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The code of the language to use, one of <paramref name="available"/> (or "en"). A valid, available <paramref name="setting"/>
    /// wins; "auto", an unknown code and an unavailable one all walk <paramref name="windowsPreferred"/> in order.
    /// </summary>
    public static string Resolve(string? setting, IReadOnlyList<string> windowsPreferred, IReadOnlySet<string> available)
    {
        if (!string.IsNullOrWhiteSpace(setting) && !setting.Trim().Equals(Auto, StringComparison.OrdinalIgnoreCase)
            && ExactKnown(Normalize(setting), available) is { } chosen)
            return chosen;
        foreach (string preferred in windowsPreferred ?? Array.Empty<string>())
            if (Match(preferred, available) is { } m) return m;
        return Fallback;
    }

    /// <summary>The available language a Windows/BCP-47 tag stands for, or null (Traditional Chinese, unknown languages, pseudo-locales).</summary>
    public static string? Match(string? tag, IReadOnlySet<string> available)
    {
        string t = Normalize(tag);
        if (!s_tag.IsMatch(t)) return null;
        if (ExactKnown(t, available) is { } exact) return exact;
        if (s_traditional.IsMatch(t)) return null;
        string baseLang = t.Split('-')[0];
        // Known order = the picker's order, so "pt" finds pt-BR and "zh" finds zh-CN.
        foreach (var k in L.Known)
            if (available.Contains(k.Code) && k.Code.Split('-')[0].Equals(baseLang, StringComparison.OrdinalIgnoreCase)) return k.Code;
        return null;
    }

    private static string? ExactKnown(string tagLower, IReadOnlySet<string> available)
    {
        foreach (var k in L.Known)
            if (k.Code.Equals(tagLower, StringComparison.OrdinalIgnoreCase) && available.Contains(k.Code)) return k.Code;
        return null;
    }

    private static string Normalize(string? tag) => (tag ?? "").Trim().Replace('_', '-').ToLowerInvariant();

    /// <summary>The Windows display-language list of the user, most preferred first ("en-US", "de-DE", ...). Empty when Windows will not say.</summary>
    public static unsafe IReadOnlyList<string> WindowsPreferred()
    {
        try
        {
            uint count = 0, size = 0;
            if (!PInvoke.GetUserPreferredUILanguages(PInvoke.MUI_LANGUAGE_NAME, &count, default, &size) || size == 0) return Array.Empty<string>();
            var buffer = new char[size];
            fixed (char* p = buffer)
            {
                if (!PInvoke.GetUserPreferredUILanguages(PInvoke.MUI_LANGUAGE_NAME, &count, p, &size)) return Array.Empty<string>();
            }
            // A double-NUL-terminated list of NUL-terminated names.
            return new string(buffer, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex)
        {
            Log.Warn("Windows display languages unavailable: " + ex.Message);
            return Array.Empty<string>();
        }
    }
}
