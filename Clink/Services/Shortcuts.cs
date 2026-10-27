using System.IO;
using Clink.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Clink.Services;

internal enum ShortcutOutcome
{
    Created,
    /// <summary>A link of ours (pointing at some Clink.exe) was rewritten.</summary>
    Replaced,
    /// <summary>A link of that name exists and points elsewhere (or cannot be read): left alone.</summary>
    LeftAlone,
    Removed,
    /// <summary>Remove: no such link.</summary>
    Missing,
    /// <summary>Remove: the link points at another exe: left alone.</summary>
    NotOurs,
    Failed,
    /// <summary>A dev folder was given and the shortcut folder (or the exe) is outside it: nothing touched.</summary>
    Refused,
}

internal readonly record struct ShortcutResult(ShortcutOutcome Outcome, string Path, string? Detail = null)
{
    public bool Ok => Outcome is ShortcutOutcome.Created or ShortcutOutcome.Replaced or ShortcutOutcome.Removed;
}

/// <summary>
/// The Start menu and Desktop shortcuts of the first-run setup (<c>Clink.lnk</c>, the names the Inno installer uses). The folders are
/// always passed in (the caller resolves the Programs and Desktop folders; tests use a temp folder). A link is written only where
/// there is none or where the existing one points at a Clink.exe; one is removed only when it points at this exe.
/// With a <c>devFolder</c> (a <c>--dev</c> instance) only folders inside it are touched. Needs a COM-initialised thread (the UI thread).
/// </summary>
internal static unsafe class Shortcuts
{
    public const string LinkName = "Clink.lnk";
    public const string ExeName = "Clink.exe";
    private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");

    public static string LinkPath(string folder) => Path.Combine(folder, LinkName);

    /// <summary>True when no dev folder is given or both paths are inside it.</summary>
    private static bool InsideDev(string? devFolder, string folder, string exePath) =>
        string.IsNullOrWhiteSpace(devFolder)
        || (InstallLocation.Under(InstallLocation.Normalize(folder), devFolder) && InstallLocation.Under(InstallLocation.Normalize(exePath), devFolder));

    /// <summary>Writes <c>folder\Clink.lnk</c> pointing at <paramref name="exePath"/>. Never throws. <paramref name="devFolder"/>: refuses a folder or exe outside it.</summary>
    public static ShortcutResult Create(string folder, string exePath, string? description = "Clink", bool notifyShell = true, string? devFolder = null)
    {
        string link = LinkPath(folder);
        if (!InsideDev(devFolder, folder, exePath)) return new ShortcutResult(ShortcutOutcome.Refused, link, "outside the dev folder");
        try
        {
            bool existed = File.Exists(link);
            if (existed)
            {
                string? current = ReadTarget(link);
                if (current == null || !Path.GetFileName(current).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    return new ShortcutResult(ShortcutOutcome.LeftAlone, link, current == null ? "unreadable link" : "points at " + current);
            }
            Directory.CreateDirectory(folder);
            Write(link, exePath, description);
            if (notifyShell) Notify(existed ? SHCNE_ID.SHCNE_UPDATEITEM : SHCNE_ID.SHCNE_CREATE, link);
            return new ShortcutResult(existed ? ShortcutOutcome.Replaced : ShortcutOutcome.Created, link);
        }
        catch (Exception ex) { return new ShortcutResult(ShortcutOutcome.Failed, link, ex.Message); }
    }

    /// <summary>Deletes <c>folder\Clink.lnk</c> only if it points at <paramref name="exePath"/>. Never throws. <paramref name="devFolder"/>: refuses a folder or exe outside it.</summary>
    public static ShortcutResult Remove(string folder, string exePath, bool notifyShell = true, string? devFolder = null)
    {
        string link = LinkPath(folder);
        if (!InsideDev(devFolder, folder, exePath)) return new ShortcutResult(ShortcutOutcome.Refused, link, "outside the dev folder");
        try
        {
            if (!File.Exists(link)) return new ShortcutResult(ShortcutOutcome.Missing, link);
            string? current = ReadTarget(link);
            if (current == null || !InstallLocation.Normalize(current).Equals(InstallLocation.Normalize(exePath), StringComparison.OrdinalIgnoreCase))
                return new ShortcutResult(ShortcutOutcome.NotOurs, link, current);
            File.Delete(link);
            if (notifyShell) Notify(SHCNE_ID.SHCNE_DELETE, link);
            return new ShortcutResult(ShortcutOutcome.Removed, link);
        }
        catch (Exception ex) { return new ShortcutResult(ShortcutOutcome.Failed, link, ex.Message); }
    }

    /// <summary>The target a .lnk points at (as stored, not resolved); null when it cannot be read.</summary>
    public static string? ReadTarget(string linkPath)
    {
        IShellLinkW* sl = null;
        IPersistFile* pf = null;
        try
        {
            sl = Create();
            pf = ComUtil.Query<IPersistFile>(sl);
            fixed (char* p = linkPath)
            {
                if (pf->Load(new PCWSTR(p), Windows.Win32.System.Com.STGM.STGM_READ).Failed) return null;
            }
            char* buf = stackalloc char[1024];
            if (sl->GetPath(new PWSTR(buf), 1024, null, 4 /* SLGP_RAWPATH */).Failed) return null;
            string s = new(buf);
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
        finally { ComUtil.Release(ref pf); ComUtil.Release(ref sl); }
    }

    private static void Write(string linkPath, string exePath, string? description)
    {
        IShellLinkW* sl = null;
        IPersistFile* pf = null;
        try
        {
            sl = Create();
            string dir = Path.GetDirectoryName(exePath) ?? "";
            fixed (char* pExe = exePath)
            fixed (char* pDir = dir)
            fixed (char* pDesc = description ?? "")
            fixed (char* pLink = linkPath)
            {
                sl->SetPath(new PCWSTR(pExe)).Check("SetPath");
                if (dir.Length > 0) sl->SetWorkingDirectory(new PCWSTR(pDir)).Check("SetWorkingDirectory");
                if (!string.IsNullOrEmpty(description)) sl->SetDescription(new PCWSTR(pDesc)).Check("SetDescription");
                sl->SetIconLocation(new PCWSTR(pExe), 0).Check("SetIconLocation");
                pf = ComUtil.Query<IPersistFile>(sl);
                pf->Save(new PCWSTR(pLink), true).Check("Save");
            }
        }
        finally { ComUtil.Release(ref pf); ComUtil.Release(ref sl); }
    }

    private static IShellLinkW* Create()
    {
        Guid clsid = CLSID_ShellLink, iid = ComUtil.Iid<IShellLinkW>();
        void* p;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &p).Check("CoCreateInstance(ShellLink)");
        return (IShellLinkW*)p;
    }

    private static void Notify(SHCNE_ID kind, string path)
    {
        try
        {
            fixed (char* p = path) PInvoke.SHChangeNotify(kind, SHCNF_FLAGS.SHCNF_PATHW | SHCNF_FLAGS.SHCNF_FLUSHNOWAIT, p, null);
        }
        catch { }
    }
}
