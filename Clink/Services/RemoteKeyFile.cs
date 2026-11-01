using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clink.Services;

/// <summary>The key file read back with other security than we asked for: it is not published.</summary>
internal sealed class RemoteKeyFileSecurityException : Exception
{
    public RemoteKeyFileSecurityException(string differs) : base("key file security differs: " + differs) { }
}

/// <summary>The key folder is not one only this user (and SYSTEM, Administrators) can change: nothing is published in it.</summary>
internal sealed class RemoteKeyFolderUntrustedException : Exception
{
    public RemoteKeyFolderUntrustedException(string why) : base("key folder not trusted: " + why) { }
}

/// <summary>Test seams of <see cref="RemoteKeyFile.Write"/> (null everywhere in Clink itself).</summary>
internal sealed class RemoteKeyFileHooks
{
    /// <summary>The owner seen for a folder something is to be made in (opened following links); null from it keeps the real owner.</summary>
    public Func<string, SecurityIdentifier?>? Owner { get; init; }
    /// <summary>Runs after the key folder was opened and checked, before the temp file is made (a test re-points a parent junction here).</summary>
    public Action? AfterCheck { get; init; }
    /// <summary>The path the temp file is made at, given the one chosen (a test redirects it, as a link swapped in at the last moment would).</summary>
    public Func<string, string>? TempPath { get; init; }
}

/// <summary>
/// The key file of the status pipe's handshake (see <see cref="RemoteProtocol.Proof"/>): <c>&lt;dir&gt;\&lt;pipeName&gt;.key</c> holds the
/// server's key as base64. Readable and writable by the user only (owner = the user SID, even when elevated; protected DACL; mandatory
/// label Medium no-write-up / no-read-up), written to a temp file in the same folder and renamed over the name, so a reader sees the old
/// file or the whole new one, never half of it. The folder must be the user's own (see <see cref="OpenTrustedDir"/>); every file is made,
/// renamed, swept and deleted inside the folder that was checked (its handle's final path, verified on each file's handle), and a file is
/// only ever deleted by the server whose key it holds.
/// </summary>
internal static class RemoteKeyFile
{
    // ---- contract with streamdeck/src/handshake.ts: change these together with the plugin's constants.

    /// <summary>While Clink serves, it touches its key file (last write time = now) this often, and at once after a resume from sleep.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The plugin treats a key file whose last write time is older than this as stale (a Clink that crashed or stopped serving) and shows
    /// "Clink off". Five refresh intervals, so a slow disk, a busy machine or a few failed refreshes never trip it.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(300);

    /// <summary>A key file that could not be written is tried again after this, doubling up to <see cref="RetryMax"/>, with no client needed.</summary>
    internal static readonly TimeSpan RetryStart = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(30);

    // ---- end of the contract

    /// <summary>The user's own folder for them, <c>%LOCALAPPDATA%\Clink\remote</c>.</summary>
    internal static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink", "remote");

    internal static string PathFor(string dir, string pipeName) => Path.Combine(dir, pipeName + ".key");

    /// <summary>The file's descriptor: owner and the one allowed account are the user, the DACL is protected, the label is Medium.</summary>
    internal static string FileSddl(SecurityIdentifier user) => "O:" + user.Value + "D:P(A;;FA;;;" + user.Value + ")S:(ML;;NWNR;;;ME)";

    /// <summary>The same for a folder made here (inherited by what is made inside it).</summary>
    internal static string DirSddl(SecurityIdentifier user) => "O:" + user.Value + "D:P(A;OICI;FA;;;" + user.Value + ")S:(ML;OICI;NWNR;;;ME)";

    private static SecurityIdentifier CurrentUser()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User ?? throw new InvalidOperationException("no user SID");
    }

    private static void CheckName(string pipeName)
    {
        if (pipeName.Length == 0 || pipeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("pipe name is not a file name", nameof(pipeName));
    }

    private static byte[] Encoded(byte[] key) => Encoding.ASCII.GetBytes(Convert.ToBase64String(key));

    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, DeleteAccess = 0x10000, FileReadData = 0x1, FileWriteAttributes = 0x100;
    private const uint ShareRead = 0x1, ShareWrite = 0x2, ShareDelete = 0x4;
    private const uint OpenExisting = 3, CreateNew = 1;
    private const uint FlagBackupSemantics = 0x02000000, FlagOpenReparsePoint = 0x00200000;

    /// <summary>
    /// Publishes <paramref name="key"/> for <paramref name="pipeName"/>. Throws when it could not: any I/O error,
    /// <see cref="RemoteKeyFolderUntrustedException"/> when the folder is not the user's own (or a file would not land in it), or
    /// <see cref="RemoteKeyFileSecurityException"/> when the file did not get the security asked for (the file is then deleted).
    /// </summary>
    /// <param name="elevated">Clink runs as administrator: folders are only made inside one the user owns (null: asks Windows).</param>
    internal static unsafe void Write(string dir, string pipeName, byte[] key, bool? elevated = null, RemoteKeyFileHooks? hooks = null)
    {
        TestGuard.Check(dir);
        CheckName(pipeName);
        var user = CurrentUser();
        byte[] sd = RemoteServer.SddlToBinary(FileSddl(user));
        var expected = new RawSecurityDescriptor(sd, 0);
        // Held until the file is in place: it does not share delete, so the folder cannot be renamed, removed or swapped for a link meanwhile,
        // and every path below is built from its final path (no link in it), not from the path we were given.
        using var folder = OpenTrustedDir(dir, user, elevated ?? Updater.IsElevated(), hooks, out string folderPath);
        SweepTemp(folderPath, pipeName);
        hooks?.AfterCheck?.Invoke();
        string name = pipeName + ".key";
        string temp = Path.Combine(folderPath, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        if (hooks?.TempPath is { } redirect) temp = redirect(temp);
        SafeFileHandle h;
        fixed (byte* p = sd)
        {
            var sa = new RemoteServer.SecurityAttributes { Length = sizeof(RemoteServer.SecurityAttributes), Descriptor = (IntPtr)p, Inherit = 0 };
            // Shares delete only: nobody else opens it while it is written, and the DELETE-only handle below can be opened next to this one.
            h = CreateFile(temp, GenericRead | GenericWrite | DeleteAccess, ShareDelete, ref sa, CreateNew, 0x80 /* NORMAL */, IntPtr.Zero);
        }
        if (h.IsInvalid) { int err = Marshal.GetLastWin32Error(); h.Dispose(); throw new Win32Exception(err); }
        SafeFileHandle? mover = null;
        bool moved = false;
        try
        {
            if (!InFolder(h, folderPath)) throw new RemoteKeyFolderUntrustedException("a file did not land in the key folder");
            RandomAccess.Write(h, Encoded(key), 0);
            RandomAccess.FlushToDisk(h);
            string? differs = RemoteServer.SecurityDifference(expected, RemoteServer.ReadSecurity(h));
            if (differs != null) throw new RemoteKeyFileSecurityException(differs);
            // The rename goes through a second handle to the same open file (no path looked up) with only DELETE, sharing everything, and
            // the writing one is closed first: from the moment the file has its name, a reader (the plugin shares read, write and delete) can open it.
            mover = ReOpenFile(h, DeleteAccess | FileReadAttributes, ShareRead | ShareWrite | ShareDelete, 0);
            if (mover.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            h.Dispose();
            RenameInto(mover, folder, name);
            moved = true;
        }
        catch (RemoteKeyFileSecurityException)
        {
            DeleteIfHolds(folderPath, name, key);   // an older file of ours must not stay either (another server's is left alone)
            throw;
        }
        finally
        {
            if (!moved) MarkDeleted(mover is { IsInvalid: false } ? mover : h);   // through its own handle: wherever it landed, only this file goes
            mover?.Dispose();
            h.Dispose();
        }
    }

    /// <summary>
    /// Removes the file if it holds <paramref name="key"/> (never another server's: one that took the pipe name over has written its own).
    /// True when it was removed; says nothing when it is not there, is not ours or cannot be removed.
    /// </summary>
    internal static bool Delete(string dir, string pipeName, byte[] key)
    {
        TestGuard.Check(dir);
        CheckName(pipeName);
        try
        {
            using var folder = OpenDirAsItself(dir, FileListDirectory | FileReadAttributes, out string folderPath);
            return folder != null && DeleteIfHolds(folderPath, pipeName + ".key", key);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The upkeep of a published file: true when it still holds <paramref name="key"/> and its last write time was set to now (what the
    /// plugin's staleness check reads). False when it is missing, a link, not ours or could not be touched: the caller writes it again.
    /// The file is opened as itself with only read data and write attributes, and only inside the key folder.
    /// </summary>
    internal static unsafe bool Refresh(string dir, string pipeName, byte[] key)
    {
        TestGuard.Check(dir);
        CheckName(pipeName);
        try
        {
            using var folder = OpenDirAsItself(dir, FileListDirectory | FileReadAttributes, out string folderPath);
            if (folder == null) return false;
            using var h = CreateFile(Path.Combine(folderPath, pipeName + ".key"), FileReadData | FileWriteAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
                OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
            if (h.IsInvalid) return false;
            if (!GetFileInformationByHandleEx(h, 9 /* FileAttributeTagInfo */, out AttributeTagInfo info, (uint)sizeof(AttributeTagInfo))) return false;
            if ((info.FileAttributes & (FileAttributeReparsePoint | FileAttributeDirectory)) != 0) return false;
            if (!InFolder(h, folderPath) || !Holds(h, key)) return false;
            long now = DateTime.UtcNow.ToFileTimeUtc();
            return SetFileTime(h, IntPtr.Zero, IntPtr.Zero, ref now);
        }
        catch (Exception) { return false; }
    }

    /// <summary>The open file holds exactly <paramref name="key"/> (base64, nothing after it).</summary>
    private static bool Holds(SafeFileHandle h, byte[] key)
    {
        byte[] want = Encoded(key);
        var got = new byte[want.Length + 1];   // one more: a longer file is not ours
        int n = 0, r;
        while (n < got.Length && (r = RandomAccess.Read(h, got.AsSpan(n), n)) > 0) n += r;
        return n == want.Length && got.AsSpan(0, n).SequenceEqual(want);
    }

    /// <summary>
    /// Reads the file and marks that same open file for deletion, so what is deleted is what was compared: a file moved over the name in
    /// between (the share mode lets it) is not touched. A link is opened as itself, never followed; a file not in the key folder is left.
    /// </summary>
    private static bool DeleteIfHolds(string folderPath, string name, byte[] key)
    {
        try
        {
            using var h = CreateFile(Path.Combine(folderPath, name), GenericRead | DeleteAccess, ShareRead | ShareDelete, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
            if (h.IsInvalid) return false;
            if (!InFolder(h, folderPath) || !Holds(h, key)) return false;
            return MarkDeleted(h);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Temp files of ours (<c>&lt;pipeName&gt;.key.&lt;32 hex&gt;.tmp</c>) that a crash between the write and the rename left behind.</summary>
    private static void SweepTemp(string folderPath, string pipeName)
    {
        string head = pipeName + ".key.";
        try
        {
            foreach (string path in Directory.EnumerateFiles(folderPath))
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(head, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                string middle = name.Substring(head.Length, name.Length - head.Length - 4);
                if (middle.Length != 32 || !middle.All(char.IsAsciiHexDigit)) continue;
                try
                {
                    using var h = CreateFile(Path.Combine(folderPath, name), DeleteAccess | FileReadAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting, FlagOpenReparsePoint, IntPtr.Zero);
                    if (!h.IsInvalid && InFolder(h, folderPath)) MarkDeleted(h);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    private static bool MarkDeleted(SafeFileHandle h)
    {
        byte delete = 1;   // FILE_DISPOSITION_INFO.DeleteFile
        try { return SetFileInformationByHandle(h, 4 /* FileDispositionInfo */, ref delete, 1); }
        catch (Exception) { return false; }
    }

    /// <summary>The open file's final path is directly inside <paramref name="folderPath"/> (the key folder handle's final path).</summary>
    private static bool InFolder(SafeFileHandle file, string folderPath)
    {
        string? at = FinalPath(file);
        if (at == null) return false;
        int slash = at.LastIndexOf('\\');
        return slash > 0 && string.Equals(at.Substring(0, slash), folderPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the handle's file really is (links resolved; <c>\\?\C:\...</c>), or null when Windows cannot say.</summary>
    private static string? FinalPath(SafeFileHandle h)
    {
        var buf = new char[512];
        while (true)
        {
            uint n = GetFinalPathNameByHandle(h, buf, (uint)buf.Length, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
            if (n == 0) return null;
            if (n < buf.Length) return new string(buf, 0, (int)n);
            if (buf.Length >= 32768) return null;
            buf = new char[Math.Min(32768, (int)n + 1)];
        }
    }

    /// <summary>
    /// Renames the open temp file to <paramref name="name"/> in the folder of <paramref name="folder"/>, replacing a file there: a handle
    /// rename relative to the checked folder, so no path is looked up again. POSIX semantics where the file system has them (a reader
    /// holding the old file open does not stop it), the plain rename otherwise. NtSetInformationFile, not SetFileInformationByHandle:
    /// kernel32 refuses a root directory handle (ERROR_INVALID_PARAMETER) and only takes a full path.
    /// </summary>
    private static unsafe void RenameInto(SafeFileHandle file, SafeFileHandle folder, string name)
    {
        // FILE_RENAME_INFORMATION: a ULONG (flags, or the BOOLEAN ReplaceIfExists), the root directory handle (pointer aligned), the
        // name's length in bytes, the name (relative to the root directory).
        int rootAt = IntPtr.Size, lengthAt = rootAt + IntPtr.Size, nameAt = lengthAt + 4, nameBytes = name.Length * 2;
        var buffer = new byte[nameAt + nameBytes + 2 * IntPtr.Size];
        bool added = false;
        folder.DangerousAddRef(ref added);
        try
        {
            fixed (byte* p = buffer)
            fixed (char* n = name)
            {
                *(IntPtr*)(p + rootAt) = folder.DangerousGetHandle();
                *(uint*)(p + lengthAt) = (uint)nameBytes;
                Buffer.MemoryCopy(n, p + nameAt, nameBytes, nameBytes);
                var status = new IoStatusBlock();
                *(uint*)p = 0x1 /* FILE_RENAME_REPLACE_IF_EXISTS */ | 0x2 /* FILE_RENAME_POSIX_SEMANTICS */;
                int st = NtSetInformationFile(file, ref status, p, (uint)buffer.Length, 65 /* FileRenameInformationEx */);
                if (st == 0) return;
                // An older Windows or a file system without the Ex class: the plain rename.
                if (st is not (unchecked((int)0xC0000003) /* INVALID_INFO_CLASS */ or unchecked((int)0xC000000D) /* INVALID_PARAMETER */ or unchecked((int)0xC00000BB) /* NOT_SUPPORTED */))
                    throw new Win32Exception(RtlNtStatusToDosError(st));
                *(uint*)p = 1;   // ReplaceIfExists = TRUE
                st = NtSetInformationFile(file, ref status, p, (uint)buffer.Length, 10 /* FileRenameInformation */);
                if (st != 0) throw new Win32Exception(RtlNtStatusToDosError(st));
            }
        }
        finally { if (added) folder.DangerousRelease(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    // ------------------------------------------------------------------ the folder

    private const uint FileListDirectory = 0x1, FileReadAttributes = 0x80, ReadControl = 0x20000, WriteDac = 0x40000, WriteOwner = 0x80000;
    private const uint FileAttributeDirectory = 0x10, FileAttributeReparsePoint = 0x400;

    /// <summary>
    /// The rights that let someone put a file in the folder, take one out, or give themselves those rights: add file / subfolder,
    /// delete child, delete, write DAC, write owner, and the generic write and all.
    /// </summary>
    private const int FolderWriteRights = 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

    private static readonly SecurityIdentifier s_system = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier s_ownerRights = new("S-1-3-4");   // the owner, who must be the user (checked first)

    /// <summary>
    /// Null when <paramref name="sd"/> (owner, DACL and mandatory label of a folder) is one only <paramref name="user"/>, SYSTEM and
    /// Administrators can change: owned by the user, no allow entry that gives anyone else a right in <see cref="FolderWriteRights"/>, and
    /// no label below Medium no-write-up. Otherwise a short reason ("owner" means it is someone else's). Deny entries only take rights away.
    /// </summary>
    internal static string? FolderProblem(RawSecurityDescriptor sd, SecurityIdentifier user)
    {
        if (sd.Owner == null || sd.Owner != user) return "owner";
        if (sd.DiscretionaryAcl == null) return "no dacl";   // a null DACL lets everyone in
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if (ace is not QualifiedAce q) return "an unknown entry";
            if (q.AceQualifier != AceQualifier.AccessAllowed || (q.AccessMask & FolderWriteRights) == 0) continue;
            var sid = q.SecurityIdentifier;
            if (sid == user || sid == s_system || sid == s_administrators || sid == s_ownerRights) continue;
            return "others can write";
        }
        if (sd.SystemAcl != null)
        {
            foreach (GenericAce ace in sd.SystemAcl)
            {
                if ((byte)ace.AceType != 0x11 /* mandatory label */ || (ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
                var b = new byte[ace.BinaryLength];
                ace.GetBinaryForm(b, 0);
                int policy = BitConverter.ToInt32(b, 4);
                var label = new SecurityIdentifier(b, 8);   // S-1-16-<level>
                if (!uint.TryParse(label.Value.AsSpan(label.Value.LastIndexOf('-') + 1), out uint level) || level < 0x2000 || (policy & 0x1 /* no-write-up */) == 0) return "label";
            }
        }
        return null;
    }

    /// <summary>
    /// Opens <paramref name="dir"/> as itself (a link is not followed) with <paramref name="access"/>, sharing read and write but not
    /// delete. Null when it does not exist. Throws <see cref="RemoteKeyFolderUntrustedException"/> for a link or other reparse point, or
    /// something that is not a folder; <paramref name="finalPath"/> is where it really is.
    /// </summary>
    private static unsafe SafeFileHandle? OpenDirAsItself(string dir, uint access, out string finalPath)
    {
        finalPath = "";
        var h = CreateFile(dir, access, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err is 2 /* FILE_NOT_FOUND */ or 3 /* PATH_NOT_FOUND */) return null;
            throw new Win32Exception(err);
        }
        try
        {
            if (!GetFileInformationByHandleEx(h, 9 /* FileAttributeTagInfo */, out AttributeTagInfo info, (uint)sizeof(AttributeTagInfo))) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0) throw new RemoteKeyFolderUntrustedException("a link or other reparse point");
            if ((info.FileAttributes & FileAttributeDirectory) == 0) throw new RemoteKeyFolderUntrustedException("not a folder");
            finalPath = FinalPath(h) ?? throw new Win32Exception(Marshal.GetLastWin32Error());
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a folder that something is to be made in, following links (a user who moved AppData to another drive with a junction keeps
    /// working), and requires that where it really is, the user is the owner: so an elevated Clink never makes a folder of the user's in
    /// a place a link pointed it to (a system folder). Held without delete sharing while the folder is made; <paramref name="finalPath"/>
    /// is where it is.
    /// </summary>
    private static unsafe SafeFileHandle OpenOwnedByUser(string dir, SecurityIdentifier user, RemoteKeyFileHooks? hooks, out string finalPath)
    {
        // Listing joins the share check (READ_CONTROL alone would not), so "no delete sharing" holds while the folder is made.
        var h = CreateFile(dir, FileListDirectory | ReadControl | FileReadAttributes, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err is 2 or 3) throw new RemoteKeyFolderUntrustedException("the folder above it is missing");
            throw new Win32Exception(err);
        }
        try
        {
            if (!GetFileInformationByHandleEx(h, 9 /* FileAttributeTagInfo */, out AttributeTagInfo info, (uint)sizeof(AttributeTagInfo))) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.FileAttributes & FileAttributeDirectory) == 0) throw new RemoteKeyFolderUntrustedException("the folder above it is not a folder");
            var owner = hooks?.Owner?.Invoke(dir) ?? RemoteServer.ReadSecurity(h).Owner;
            if (owner == null || owner != user) throw new RemoteKeyFolderUntrustedException("the folder above it is not the user's");
            finalPath = FinalPath(h) ?? throw new Win32Exception(Marshal.GetLastWin32Error());
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>Makes <paramref name="path"/> with <paramref name="sd"/>; one that is there already is fine (it is checked after).</summary>
    private static unsafe void MakeDir(string path, byte[] sd)
    {
        fixed (byte* p = sd)
        {
            var sa = new RemoteServer.SecurityAttributes { Length = sizeof(RemoteServer.SecurityAttributes), Descriptor = (IntPtr)p, Inherit = 0 };
            if (CreateDirectory(path, ref sa)) return;
        }
        int err = Marshal.GetLastWin32Error();
        if (err != 183 /* ERROR_ALREADY_EXISTS */) throw new Win32Exception(err);
    }

    /// <summary>
    /// Makes the missing key folder (owner-only protected descriptor). Not elevated, the user's own token makes it and its parent, so it
    /// cannot plant anything the user could not. Elevated, a folder is only made inside one the user owns (<see cref="OpenOwnedByUser"/>),
    /// through that folder's final path while it is held: the parent (<c>Clink</c>) gets the user as owner and inherits the rest.
    /// </summary>
    private static void MakeKeyDir(string dir, SecurityIdentifier user, bool elevated, RemoteKeyFileHooks? hooks)
    {
        byte[] dirSd = RemoteServer.SddlToBinary(DirSddl(user));
        string full = Path.GetFullPath(dir).TrimEnd('\\');
        string parent = Path.GetDirectoryName(full) ?? throw new RemoteKeyFolderUntrustedException("no folder above it");
        if (!elevated)
        {
            Directory.CreateDirectory(parent);
            MakeDir(full, dirSd);
            return;
        }
        if (!Directory.Exists(parent))
        {
            string grand = Path.GetDirectoryName(parent) ?? throw new RemoteKeyFolderUntrustedException("no folder above it");
            using var g = OpenOwnedByUser(grand, user, hooks, out string grandPath);
            MakeDir(Path.Combine(grandPath, Path.GetFileName(parent)), RemoteServer.SddlToBinary("O:" + user.Value));
        }
        using var p = OpenOwnedByUser(parent, user, hooks, out string parentPath);
        MakeDir(Path.Combine(parentPath, Path.GetFileName(full)), dirSd);
    }

    /// <summary>
    /// Makes <paramref name="dir"/> when it is missing (<see cref="MakeKeyDir"/>), then opens it as itself (a junction or other reparse
    /// point is not followed, and refused) and checks it with <see cref="FolderProblem"/>. A folder of the user's that is weaker gets
    /// <see cref="DirSddl"/> again; one that is someone else's, a link, or still weak is refused (<see cref="RemoteKeyFolderUntrustedException"/>).
    /// Its parents may be junctions a user made (moving AppData to another drive); what is written is put in this folder through its final
    /// path. The handle returned does not share delete.
    /// </summary>
    private static unsafe SafeFileHandle OpenTrustedDir(string dir, SecurityIdentifier user, bool elevated, RemoteKeyFileHooks? hooks, out string finalPath)
    {
        byte[] dirSd = RemoteServer.SddlToBinary(DirSddl(user));
        if (!Directory.Exists(dir)) MakeKeyDir(dir, user, elevated, hooks);
        // As much as we may: changing the DACL needs WRITE_DAC, the label WRITE_OWNER. Listing joins the share check, so "no delete sharing" holds.
        const uint baseAccess = FileListDirectory | FileReadAttributes | ReadControl;
        SafeFileHandle? h = null;
        uint granted = 0;
        string at = "";
        foreach (uint access in new[] { baseAccess | WriteDac | WriteOwner, baseAccess | WriteDac, baseAccess })
        {
            try { h = OpenDirAsItself(dir, access, out at); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 5 /* ERROR_ACCESS_DENIED */) { continue; }
            if (h == null) throw new Win32Exception(3 /* PATH_NOT_FOUND */);
            granted = access;
            break;
        }
        if (h == null) throw new RemoteKeyFolderUntrustedException("no access");
        finalPath = at;
        try
        {
            string? problem = FolderProblem(RemoteServer.ReadSecurity(h), user);
            if (problem == null) return h;
            if (problem == "owner" || (granted & WriteDac) == 0) throw new RemoteKeyFolderUntrustedException(problem);
            // Ours but weaker than asked for: our descriptor again, on this same open folder (DACL, and the label where WRITE_OWNER was granted).
            uint what = 0x4 /* DACL */ | ((granted & WriteOwner) != 0 ? 0x10u /* LABEL */ : 0);
            if (!SetKernelObjectSecurity(h, what, dirSd)) throw new RemoteKeyFolderUntrustedException(problem);
            problem = FolderProblem(RemoteServer.ReadSecurity(h), user);
            if (problem != null) throw new RemoteKeyFolderUntrustedException(problem);
            Log.Info("remote: the key folder could be changed by others: its security was set again");
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>Owner, DACL and label of a file as Windows holds them (opens it for reading).</summary>
    internal static RawSecurityDescriptor ReadSecurity(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return RemoteServer.ReadSecurity(fs.SafeFileHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref RemoteServer.SecurityAttributes security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectory(string name, ref RemoteServer.SecurityAttributes security);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint size);

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtSetInformationFile(SafeFileHandle file, ref IoStatusBlock status, byte* info, uint length, int infoClass);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTagInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint access, uint share, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle file, IntPtr creation, IntPtr lastAccess, ref long lastWrite);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation, byte[] securityDescriptor);
}
