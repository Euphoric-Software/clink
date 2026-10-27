using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace Clink.Services;

/// <summary>Whether this process runs with an elevated token (Run as administrator, or an admin account with UAC off).</summary>
internal static unsafe class Elevation
{
    /// <summary>Stands in for the answer in the unit tests; null = ask Windows. A test that sets it puts it back.</summary>
    internal static bool? IsElevatedOverride { get; set; }

    /// <summary>Stands in for the token query in the unit tests: returns the answer, null = "could not be read", or throws. Null = the real query.</summary>
    internal static Func<bool?>? TokenQueryOverride { get; set; }

    /// <summary>Stands in for the Administrators-group check in the unit tests (may throw). Null = the real check.</summary>
    internal static Func<bool>? PrincipalQueryOverride { get; set; }

    /// <summary>
    /// True when the process token is elevated (<c>GetTokenInformation(TokenElevation)</c>). Not the same as "is in the Administrators group":
    /// a standard-token process of an administrator is not elevated. Fails CLOSED: when the token cannot be read it falls back to the
    /// Administrators-group check, and when that fails too the answer is true (elevated), so a doubt never lets an elevated process install,
    /// update or write the user's Run key.
    /// </summary>
    public static bool IsElevated()
    {
        if (IsElevatedOverride is bool forced) return forced;
        try { if (QueryToken() is bool known) return known; } catch { }
        try { return QueryPrincipal(); } catch { return true; }
    }

    private static bool? QueryToken()
    {
        if (TokenQueryOverride is { } fake) return fake();
        HANDLE token;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_QUERY, &token)) return null;
        try
        {
            TOKEN_ELEVATION elevation = default;
            uint returned = 0;
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, &elevation, (uint)sizeof(TOKEN_ELEVATION), &returned)) return null;
            return elevation.TokenIsElevated != 0;
        }
        finally { PInvoke.CloseHandle(token); }
    }

    private static bool QueryPrincipal()
    {
        if (PrincipalQueryOverride is { } fake) return fake();
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
