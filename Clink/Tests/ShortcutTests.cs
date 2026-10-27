using System.IO;
using Clink.Services;

namespace Clink.Tests;

/// <summary>.lnk files written and read back, only inside a temp folder (never the real Start menu or Desktop).</summary>
internal static class ShortcutTests
{
    [UnitTest]
    public static void A_shortcut_round_trips_its_target()
    {
        using var dir = new TempDir();
        string exe = dir.File(@"app\Clink.exe");
        string folder = dir.File("StartMenu");
        var r = Shortcuts.Create(folder, exe, notifyShell: false);
        Check.Equal(ShortcutOutcome.Created, r.Outcome, r.Detail);
        Check.Equal(Path.Combine(folder, "Clink.lnk"), r.Path);
        Check.True(File.Exists(r.Path));
        Check.Equal(exe, Shortcuts.ReadTarget(r.Path), "the target as stored");
    }

    [UnitTest]
    public static void An_existing_link_to_a_clink_exe_is_overwritten()
    {
        using var dir = new TempDir();
        string folder = dir.File("Desktop");
        Shortcuts.Create(folder, dir.File(@"old\Clink.exe"), notifyShell: false);
        string newExe = dir.File(@"new\Clink.exe");
        var r = Shortcuts.Create(folder, newExe, notifyShell: false);
        Check.Equal(ShortcutOutcome.Replaced, r.Outcome, r.Detail);
        Check.Equal(newExe, Shortcuts.ReadTarget(r.Path));
    }

    [UnitTest]
    public static void A_link_of_that_name_to_something_else_is_left_alone()
    {
        using var dir = new TempDir();
        string folder = dir.File("Desktop");
        string other = dir.File(@"tools\Notepad.exe");
        Shortcuts.Create(folder, other, notifyShell: false);   // nothing exists yet, so the first Create writes this link to a non-Clink exe; the second Create must then leave it alone
        var r = Shortcuts.Create(folder, dir.File(@"new\Clink.exe"), notifyShell: false);
        Check.Equal(ShortcutOutcome.LeftAlone, r.Outcome);
        Check.Equal(other, Shortcuts.ReadTarget(r.Path), "unchanged");
    }

    [UnitTest]
    public static void A_file_that_is_not_a_link_is_left_alone()
    {
        using var dir = new TempDir();
        string folder = dir.File("Desktop");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Clink.lnk"), "not a shortcut");
        var r = Shortcuts.Create(folder, dir.File(@"new\Clink.exe"), notifyShell: false);
        Check.Equal(ShortcutOutcome.LeftAlone, r.Outcome);
        Check.Equal("not a shortcut", File.ReadAllText(r.Path));
        Check.Null(Shortcuts.ReadTarget(r.Path));
    }

    [UnitTest]
    public static void Remove_deletes_only_a_link_that_points_at_this_exe()
    {
        using var dir = new TempDir();
        string folder = dir.File("StartMenu");
        string mine = dir.File(@"a\Clink.exe"), other = dir.File(@"b\Clink.exe");

        Check.Equal(ShortcutOutcome.Missing, Shortcuts.Remove(folder, mine, notifyShell: false).Outcome);

        Shortcuts.Create(folder, other, notifyShell: false);
        var notOurs = Shortcuts.Remove(folder, mine, notifyShell: false);
        Check.Equal(ShortcutOutcome.NotOurs, notOurs.Outcome);
        Check.True(File.Exists(notOurs.Path));

        Shortcuts.Create(folder, mine, notifyShell: false);
        var removed = Shortcuts.Remove(folder, mine.ToUpperInvariant(), notifyShell: false);
        Check.Equal(ShortcutOutcome.Removed, removed.Outcome, removed.Detail);
        Check.False(File.Exists(removed.Path));
    }

    [UnitTest]
    public static void Remove_leaves_an_unreadable_file_alone()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        string link = Path.Combine(dir.Path, "Clink.lnk");
        File.WriteAllText(link, "junk");
        Check.Equal(ShortcutOutcome.NotOurs, Shortcuts.Remove(dir.Path, dir.File("Clink.exe"), notifyShell: false).Outcome);
        Check.True(File.Exists(link));
    }

    [UnitTest]
    public static void A_dev_folder_refuses_anything_outside_it()
    {
        using var dir = new TempDir();
        string dev = dir.File("dev");
        string inside = Path.Combine(dev, "Programs"), outside = dir.File("StartMenu");
        string devExe = Path.Combine(dev, "Clink.exe");

        var refused = Shortcuts.Create(outside, devExe, notifyShell: false, devFolder: dev);
        Check.Equal(ShortcutOutcome.Refused, refused.Outcome);
        Check.False(Directory.Exists(outside), "nothing created outside");
        Check.Equal(ShortcutOutcome.Refused, Shortcuts.Create(inside, dir.File("real\\Clink.exe"), notifyShell: false, devFolder: dev).Outcome, "exe outside");
        Check.False(Directory.Exists(inside));

        Check.Equal(ShortcutOutcome.Created, Shortcuts.Create(inside, devExe, notifyShell: false, devFolder: dev).Outcome);
        Check.Equal(ShortcutOutcome.Refused, Shortcuts.Create(Path.Combine(dev + "X", "p"), devExe, notifyShell: false, devFolder: dev).Outcome, "a sibling that starts with the same letters");

        Shortcuts.Create(outside, devExe, notifyShell: false);   // a real-looking link outside the dev folder
        Check.Equal(ShortcutOutcome.Refused, Shortcuts.Remove(outside, devExe, notifyShell: false, devFolder: dev).Outcome);
        Check.True(File.Exists(Shortcuts.LinkPath(outside)), "left in place");
        Check.Equal(ShortcutOutcome.Removed, Shortcuts.Remove(inside, devExe, notifyShell: false, devFolder: dev).Outcome);
    }

    // ------------------------------------------------------------------ the replace rule (slice 3a)

    private static string Touch(TempDir dir, string name)
    {
        string p = dir.File(name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "exe");
        return p;
    }

    [UnitTest]
    public static void A_link_to_another_clink_exe_that_exists_is_left_alone()
    {
        using var dir = new TempDir();
        string folder = dir.File("StartMenu");
        string installed = Touch(dir, @"Programs\Clink\Clink.exe");   // the installer's copy
        string download = Touch(dir, @"Downloads\Clink.exe");
        Shortcuts.Create(folder, installed, notifyShell: false);
        var r = Shortcuts.Create(folder, download, notifyShell: false);
        Check.Equal(ShortcutOutcome.LeftAlone, r.Outcome, r.Detail);
        Check.Equal(installed, Shortcuts.ReadTarget(r.Path), "still the installed copy");
    }

    [UnitTest]
    public static void A_link_to_this_exe_or_a_clink_exe_that_is_gone_is_replaced()
    {
        using var dir = new TempDir();
        string folder = dir.File("StartMenu");
        string me = Touch(dir, @"new\Clink.exe");

        Shortcuts.Create(folder, me, notifyShell: false);
        Check.Equal(ShortcutOutcome.Replaced, Shortcuts.Create(folder, me, notifyShell: false).Outcome, "this exe");
        Check.Equal(ShortcutOutcome.Replaced, Shortcuts.Create(folder, me.ToUpperInvariant(), notifyShell: false).Outcome, "this exe, another spelling");

        string gone = dir.File(@"deleted\Clink.exe");   // never created: the link is stale
        Shortcuts.Create(dir.File("Other"), gone, notifyShell: false);
        var stale = Shortcuts.Create(dir.File("Other"), me, notifyShell: false);
        Check.Equal(ShortcutOutcome.Replaced, stale.Outcome, stale.Detail);
        Check.Equal(me, Shortcuts.ReadTarget(stale.Path));
    }

    [UnitTest]
    public static void A_link_to_the_previous_exe_is_replaced_only_when_it_is_named()
    {
        using var dir = new TempDir();
        string folder = dir.File("Desktop");
        string previous = Touch(dir, @"Downloads\Clink.exe");
        string me = Touch(dir, @"Programs\Clink\Clink.exe");
        Shortcuts.Create(folder, previous, notifyShell: false);

        Check.Equal(ShortcutOutcome.LeftAlone, Shortcuts.Create(folder, me, notifyShell: false).Outcome, "an existing copy elsewhere, previousExe not given");
        Check.Equal(ShortcutOutcome.LeftAlone, Shortcuts.Create(folder, me, notifyShell: false, previousExe: dir.File("Other.exe")).Outcome, "another exe named");
        var r = Shortcuts.Create(folder, me, notifyShell: false, previousExe: previous.ToUpperInvariant());
        Check.Equal(ShortcutOutcome.Replaced, r.Outcome, r.Detail);
        Check.Equal(me, Shortcuts.ReadTarget(r.Path));
    }

    [UnitTest]
    public static void A_link_to_a_share_or_mapped_drive_is_not_ours_and_never_probed()
    {
        // The decision on plain strings: no .lnk, no file access, no shell, so a network name is never resolved (LLMNR / NetBIOS / SMB).
        string me = @"C:\Programs\Clink\Clink.exe";
        foreach (string remote in new[] { @"\\srv\share\Clink.exe", @"\\?\UNC\srv\share\Clink.exe" })
        {
            Check.False(Shortcuts.MayReplace(remote, me, null), remote + " is not ours");
            Check.False(Shortcuts.MayReplace(remote, me, @"C:\Other\Clink.exe"), remote + " with another previous exe");
            Check.True(Shortcuts.MayReplace(remote, remote, null), "the link already is the target (no file access needed)");
            Check.True(Shortcuts.MayReplace(remote, me, remote), "the named previous exe");
        }

        var before = Shortcuts.DriveTypeOf;
        try
        {
            string stale = @"C:\NoSuchClinkTestFolder\deleted\Clink.exe";   // not on disk: stale on a fixed drive, but a mapped one is never probed
            Shortcuts.DriveTypeOf = _ => 3;
            Check.True(Shortcuts.MayReplace(stale, me, null), "fixed drive, missing Clink.exe: stale");
            Shortcuts.DriveTypeOf = _ => 4;
            Check.False(Shortcuts.MayReplace(stale, me, null), "mapped network drive: not ours");
            Shortcuts.DriveTypeOf = _ => 2;
            Check.False(Shortcuts.MayReplace(stale, me, null), "removable drive: not ours");
            Shortcuts.DriveTypeOf = _ => throw new InvalidOperationException("no such drive");
            Check.False(Shortcuts.MayReplace(stale, me, null), "an unreadable drive type: not ours");
        }
        finally { Shortcuts.DriveTypeOf = before; }

        using var dir = new TempDir();   // real links, local targets only
        try
        {
            string me2 = Touch(dir, @"new\Clink.exe");
            string gone = dir.File(@"deleted\Clink.exe");   // not on disk: a stale link on a fixed drive, but this "drive" is a mapped one
            Shortcuts.Create(dir.File("Mapped"), gone, notifyShell: false);
            me = me2;
            Shortcuts.DriveTypeOf = _ => 4;
            var mapped = Shortcuts.Create(dir.File("Mapped"), me, notifyShell: false);
            Check.Equal(ShortcutOutcome.LeftAlone, mapped.Outcome, mapped.Detail);
            Shortcuts.DriveTypeOf = before;
            Check.Equal(ShortcutOutcome.Replaced, Shortcuts.Create(dir.File("Mapped"), me, notifyShell: false).Outcome, "the same link on a fixed drive is stale and replaced");
        }
        finally { Shortcuts.DriveTypeOf = before; }
    }

    [UnitTest]
    public static void A_link_to_a_missing_program_that_is_not_clink_is_still_left_alone()
    {
        using var dir = new TempDir();
        string folder = dir.File("Desktop");
        string other = dir.File(@"tools\Notepad.exe");   // not on disk, not named Clink.exe
        Shortcuts.Create(folder, other, notifyShell: false);
        Check.Equal(ShortcutOutcome.LeftAlone, Shortcuts.Create(folder, Touch(dir, @"new\Clink.exe"), notifyShell: false).Outcome);
        Check.Equal(other, Shortcuts.ReadTarget(Shortcuts.LinkPath(folder)));
    }
}
