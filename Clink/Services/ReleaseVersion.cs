using System.Text.RegularExpressions;

namespace Clink.Services;

/// <summary>
/// A Clink version: three-part number plus an optional SemVer pre-release suffix ("beta.1", "dev.57"). Dev builds published
/// from the dev branch are pre-releases named <c>X.Y.Z-dev.N</c>. Ordering: by number; at the same number a release beats any
/// pre-release; two dev builds compare by N (numerically); other pre-releases compare ordinally.
/// </summary>
internal readonly record struct ReleaseVersion(Version Number, string? Prerelease) : IComparable<ReleaseVersion>
{
    // No leading zeros: "dev.07" is not a dev build, so two versions that order equal are also equal (record equality compares the text).
    private static readonly Regex s_dev = new(@"^dev\.(0|[1-9]\d*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Version s_zero = new(0, 0, 0);

    /// <summary><see cref="Number"/>, or 0.0.0 for <c>default(ReleaseVersion)</c> (whose Number is null).</summary>
    private Version Num => Number ?? s_zero;

    /// <summary>N of a "dev.N" pre-release, otherwise null.</summary>
    public int? DevBuild
    {
        get
        {
            if (Prerelease == null) return null;
            var m = s_dev.Match(Prerelease);
            return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : null;
        }
    }

    public bool IsDevBuild => DevBuild != null;
    public bool IsPrerelease => Prerelease != null;

    /// <summary>"v1.2.3", "1.2.3+abc123" → 1.2.3; "1.2.3-beta.1+abc" → 1.2.3-beta.1; null if not a version.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        int cut = s.IndexOfAny(new[] { '+', ' ' });
        if (cut >= 0) s = s[..cut];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; if (pre.Length == 0) pre = null; }
        if (!Version.TryParse(s, out var v)) return null;
        return new ReleaseVersion(new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build)), pre);
    }

    public int CompareTo(ReleaseVersion other)
    {
        int c = Num.CompareTo(other.Num);
        if (c != 0) return c;
        if (Prerelease == null) return other.Prerelease == null ? 0 : 1;
        if (other.Prerelease == null) return -1;
        if (DevBuild is int a && other.DevBuild is int b) return a.CompareTo(b);
        return Math.Sign(string.CompareOrdinal(Prerelease, other.Prerelease));
    }

    public override string ToString() => Prerelease == null ? Num.ToString(3) : $"{Num.ToString(3)}-{Prerelease}";

    public static bool operator <(ReleaseVersion l, ReleaseVersion r) => l.CompareTo(r) < 0;
    public static bool operator >(ReleaseVersion l, ReleaseVersion r) => l.CompareTo(r) > 0;
    public static bool operator <=(ReleaseVersion l, ReleaseVersion r) => l.CompareTo(r) <= 0;
    public static bool operator >=(ReleaseVersion l, ReleaseVersion r) => l.CompareTo(r) >= 0;
}
