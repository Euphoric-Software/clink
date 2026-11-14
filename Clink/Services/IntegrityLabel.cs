using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clink.Services;

/// <summary>
/// Who wrote a file or folder, as far as its security descriptor says: its mandatory integrity label and its owner. A file that a
/// sandboxed (AppContainer) or other low-integrity process created carries a label below Medium; a file without a label counts as
/// Medium. Used to decide whether another copy's settings may be trusted (<see cref="SettingsImport.FindForeignCopies"/>), and to read
/// such a copy's files so that what is read is what was checked (<see cref="ReadTrusted"/>).
/// </summary>
internal static class IntegrityLabel
{
    /// <summary>The Medium integrity RID (<c>SECURITY_MANDATORY_MEDIUM_RID</c>): a normal process of the signed-in user.</summary>
    internal const int MediumRid = 0x2000;

    // Owners besides the current user that a file of this user's profile may legitimately have: what an elevated or system process
    // (an installer, Windows itself) creates. None of them is a sandboxed writer.
    private static readonly SecurityIdentifier[] s_trustedOwners =
    {
        new("S-1-5-32-544"),                                                    // Administrators
        new("S-1-5-18"),                                                        // SYSTEM
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),  // TrustedInstaller
    };

    /// <summary>
    /// Why <paramref name="path"/> (a file or folder) must not be trusted, or null when it may: it is a reparse point (a junction or
    /// a link: it would be judged by where it points), it is labelled below Medium, its owner is not the current user (nor
    /// Administrators, SYSTEM or TrustedInstaller), or its owner and label cannot be read. Never throws.
    /// </summary>
    internal static string? WhyUntrusted(string path)
    {
        using var h = OpenNoFollow(path, READ_CONTROL | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, out int error);
        if (h.IsInvalid) return $"it cannot be opened (error {error.ToString(CultureInfo.InvariantCulture)})";
        return WhyUntrusted(h);
    }

    /// <summary><see cref="WhyUntrusted(string)"/> for an open handle (opened with READ_CONTROL and without following a reparse point).</summary>
    internal static string? WhyUntrusted(SafeFileHandle handle)
    {
        try
        {
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0) return "it is a junction or a link";
            uint error = GetSecurityInfo(handle, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION,
                out nint ownerSid, out _, out _, out nint sacl, out nint sd);
            if (error != 0) return $"its owner and integrity label cannot be read (error {error.ToString(CultureInfo.InvariantCulture)})";
            try
            {
                var owner = ownerSid != 0 ? new SecurityIdentifier(ownerSid) : null;
                using var me = WindowsIdentity.GetCurrent();
                return Judge(LowestRid(sacl), owner, me.User);
            }
            finally { LocalFree(sd); }
        }
        catch (Exception ex) { return "its owner and integrity label cannot be read: " + ex.Message; }
    }

    /// <summary>The decision of <see cref="WhyUntrusted(string)"/>: <paramref name="rid"/> is the label's RID (null: no label, which is Medium).</summary>
    internal static string? Judge(int? rid, SecurityIdentifier? owner, SecurityIdentifier? currentUser)
    {
        if (rid is int r && r < MediumRid)
            return $"it is labelled {(r >= 0x1000 ? "Low" : "Untrusted")} integrity (a sandboxed or low-integrity process wrote it)";
        if (owner == null || !((currentUser != null && owner.Equals(currentUser)) || s_trustedOwners.Any(owner.Equals))) return "it is not owned by the current user";
        return null;
    }

    /// <summary>
    /// The lowest integrity RID among the mandatory label ACEs of <paramref name="sacl"/> that apply to the object itself (an
    /// inherit-only ACE only labels what is created inside it); null when there is none (Medium). A label that cannot be read counts as 0.
    /// </summary>
    internal static int? LowestRid(nint sacl)
    {
        if (sacl == 0) return null;
        // ACL: AclRevision(1) Sbz1(1) AclSize(2) AceCount(2) Sbz2(2), then the ACEs: AceType(1) AceFlags(1) AceSize(2) Mask(4) Sid.
        int size = (ushort)Marshal.ReadInt16(sacl, 2), count = (ushort)Marshal.ReadInt16(sacl, 4), at = 8;
        int? lowest = null;
        for (int i = 0; i < count && at + 8 < size; i++)
        {
            byte type = Marshal.ReadByte(sacl, at), flags = Marshal.ReadByte(sacl, at + 1);
            int aceSize = (ushort)Marshal.ReadInt16(sacl, at + 2);
            if (aceSize < 8 || at + aceSize > size) break;
            if (type == SYSTEM_MANDATORY_LABEL_ACE_TYPE && (flags & INHERIT_ONLY_ACE) == 0)
            {
                int rid = 0;   // a label we cannot read counts as the lowest
                string sid = new SecurityIdentifier(sacl + at + 8).Value;   // S-1-16-<rid>
                if (sid.StartsWith("S-1-16-", StringComparison.Ordinal) && uint.TryParse(sid[7..], NumberStyles.None, CultureInfo.InvariantCulture, out uint value))
                    rid = (int)Math.Min(value, int.MaxValue);
                lowest = lowest is int l ? Math.Min(l, rid) : rid;
            }
            at += aceSize;
        }
        return lowest;
    }

    /// <summary>What <see cref="ReadTrusted"/> found: the text, or that the file is missing, too big (<see cref="Length"/>) or refused (why).</summary>
    internal readonly record struct TrustedText(string? Text, bool Missing, long Length, string? Refused);

    /// <summary>
    /// Reads <paramref name="fileName"/> in <paramref name="folder"/> so that what is read is what was checked: the folder is opened
    /// first and held (it cannot be renamed, replaced or turned into a link meanwhile), then the file, without following a reparse
    /// point and shared for reading only; both are judged on their handles (<see cref="WhyUntrusted(SafeFileHandle)"/>) and the text
    /// is read from the same file handle. More than <paramref name="maxBytes"/> is not read. Never throws.
    /// </summary>
    internal static TrustedText ReadTrusted(string folder, string fileName, long maxBytes)
    {
        try
        {
            using var dir = OpenNoFollow(folder, READ_CONTROL | FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, out int dirError);
            if (dir.IsInvalid)
                return dirError is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND ? new(null, true, 0, null)
                    : new(null, false, 0, $"its folder cannot be opened (error {dirError.ToString(CultureInfo.InvariantCulture)})");
            if (WhyUntrusted(dir) is { } dirWhy) return new(null, false, 0, "its folder: " + dirWhy);

            using var file = OpenNoFollow(Path.Combine(folder, fileName), GENERIC_READ, FILE_SHARE_READ, out int error);
            if (file.IsInvalid)
                return error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND ? new(null, true, 0, null)
                    : new(null, false, 0, $"it cannot be opened (error {error.ToString(CultureInfo.InvariantCulture)})");
            if ((File.GetAttributes(file) & FileAttributes.Directory) != 0) return new(null, false, 0, "it is a folder");
            if (WhyUntrusted(file) is { } why) return new(null, false, 0, why);
            long length = RandomAccess.GetLength(file);
            if (length > maxBytes) return new(null, false, length, null);
            using var stream = new FileStream(file, FileAccess.Read, 1);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new(reader.ReadToEnd(), false, length, null);
        }
        catch (Exception ex) { return new(null, false, 0, "it cannot be read: " + ex.Message); }
    }

    private static SafeFileHandle OpenNoFollow(string path, uint access, uint share, out int error)
    {
        var h = CreateFile(AppDataRedirect.ExtendedPath(path), access, share, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        error = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return h;
    }

    private const int SE_FILE_OBJECT = 1;
    private const uint OWNER_SECURITY_INFORMATION = 0x1, LABEL_SECURITY_INFORMATION = 0x10;
    private const byte SYSTEM_MANDATORY_LABEL_ACE_TYPE = 0x11, INHERIT_ONLY_ACE = 0x08;
    private const uint GENERIC_READ = 0x80000000, READ_CONTROL = 0x00020000, FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint mem);
}
