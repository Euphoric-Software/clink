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
}
