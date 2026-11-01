using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Clink.Services;

namespace Clink.Tests;

// ShareCache: the keys, the paths and names, the reuse test, the cleanup (age, size cap, in use, locked and read-only files, hard links,
// names ending with a dot or space, junctions and links inside, above and swapped in mid-cleanup, folders that are not entries, an 8.3
// root, an elevated process, which never writes, reuses or cleans), the in-use marks against a delete in progress, the schedule and the
// root guard. A fake clock and small caps stand in for 24 hours and 5 GB; the files are a few bytes.
internal static class ShareCacheTests
{
    private static readonly DateTime Now = new(2030, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    private static ShareCache Make(TempDir dir, TimeSpan? maxAge = null, long? maxBytes = null, string name = "cache") =>
        new(dir.File(name), () => Now, maxAge, maxBytes, isElevated: () => false);

    /// <summary>A finished copy under key <paramref name="key"/> with <paramref name="bytes"/> bytes last written <paramref name="age"/> before Now.</summary>
    private static string Copy(ShareCache cache, string key, int bytes, TimeSpan age, string name = "clip.mp4")
    {
        string path = cache.PathFor(key, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    private static string K(char c) => new string(c, 32);

    private static readonly TimeSpan Old = TimeSpan.FromHours(30);

    [UnitTest]
    public static void Key_is_stable_and_sensitive_to_every_part()
    {
        var t = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        string a = ShareCache.Key(@"C:\Clips\a.mp4", 1000, t, 20_000_000, "h264", "720p60");
        Check.Equal(a, ShareCache.Key(@"C:\Clips\a.mp4", 1000, t, 20_000_000, "h264", "720p60"));
        Check.Equal(a, ShareCache.Key(@"C:\Clips\.\a.mp4", 1000, t, 20_000_000, "H264", "720p60"), "path is normalized, codec case does not matter");
        Check.Equal(32, a.Length);
        Check.True(ShareCache.IsKey(a), "a key is 32 lowercase hex characters");
        var others = new[]
        {
            ShareCache.Key(@"C:\Clips\b.mp4", 1000, t, 20_000_000, "h264", "720p60"),
            ShareCache.Key(@"C:\Clips\a.mp4", 1001, t, 20_000_000, "h264", "720p60"),
            ShareCache.Key(@"C:\Clips\a.mp4", 1000, t.AddSeconds(1), 20_000_000, "h264", "720p60"),
            ShareCache.Key(@"C:\Clips\a.mp4", 1000, t, 50_000_000, "h264", "720p60"),
            ShareCache.Key(@"C:\Clips\a.mp4", 1000, t, 20_000_000, "hevc", "720p60"),
            ShareCache.Key(@"C:\Clips\a.mp4", 1000, t, 20_000_000, "h264", "720p30"),
            ShareCache.Key(@"C:\Clips\a.mp4", 1000, t, 20_000_000, "h264"),
        };
        Check.Equal(others.Length, others.Append(a).Distinct().Count() - 1, "every change gives a new key");
        Check.False(others.Contains(a));
    }

    [UnitTest]
    public static void An_unspecified_kind_time_is_taken_as_UTC()
    {
        var utc = new DateTime(2030, 1, 1, 8, 30, 0, DateTimeKind.Utc);
        string k = ShareCache.Key(@"C:\Clips\a.mp4", 1000, utc, 20_000_000, "h264");
        Check.Equal(k, ShareCache.Key(@"C:\Clips\a.mp4", 1000, DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), 20_000_000, "h264"), "unspecified = UTC, not shifted by the time zone");
        Check.Equal(k, ShareCache.Key(@"C:\Clips\a.mp4", 1000, utc.ToLocalTime(), 20_000_000, "h264"), "a local time is converted");
    }

    [UnitTest]
    public static void KeyFor_reads_the_file_and_changes_with_it()
    {
        using var dir = new TempDir();
        string f = dir.File("src.mp4");
        File.WriteAllBytes(f, new byte[10]);
        File.SetLastWriteTimeUtc(f, Now);
        string k1 = ShareCache.KeyFor(f, 1000, "h264");
        Check.Equal(k1, ShareCache.KeyFor(f, 1000, "h264"));
        File.SetLastWriteTimeUtc(f, Now.AddMinutes(1));
        Check.NotEqual(k1, ShareCache.KeyFor(f, 1000, "h264"));
    }

    [UnitTest]
    public static void Path_keeps_the_original_name_in_a_key_folder()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string p = cache.PathFor(K('a'), "My Clip 2030-01-01.mp4");
        Check.Equal(Path.Combine(cache.Root, K('a'), "My Clip 2030-01-01.mp4"), p);
        Check.Equal("clip.mp4", Path.GetFileName(cache.PathFor(K('a'), "")));
        Check.Equal("a_b.mp4", Path.GetFileName(cache.PathFor(K('a'), "a?b.mkv")));
        Check.Equal("x.mp4", Path.GetFileName(cache.PathFor(K('a'), "x. .mkv")), "no trailing dot or space");
        Check.Throws<ArgumentException>(() => cache.PathFor(@"..\x", "a.mp4"), "a key is hex only");
        Check.Throws<ArgumentException>(() => cache.PathFor(K('A'), "a.mp4"), "lowercase only");
        Check.Throws<ArgumentException>(() => cache.PathFor("abc", "a.mp4"), "exactly 32 characters");
        Check.Throws<ArgumentException>(() => cache.MarkInUse("abc"));
        Check.False(ShareCache.IsKey(null));
        Check.False(ShareCache.IsKey(new string('a', 31)));
        Check.False(ShareCache.IsKey(new string('a', 33)));
        Check.False(ShareCache.IsKey(new string('g', 32)));
        Check.True(ShareCache.IsKey("0123456789abcdef0123456789abcdef"));
    }

    [UnitTest]
    public static void A_long_name_is_cut_so_the_part_path_fits()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string longName = new string('x', 250) + ".mkv";
        string part = cache.PartPathFor(K('a'), longName);
        Check.True(part.Length <= 259, $"the part path is {part.Length} characters");
        string name = Path.GetFileName(cache.PathFor(K('a'), longName));
        Check.True(name.EndsWith(".mp4", StringComparison.Ordinal), name);
        Check.True(name.StartsWith("xxxxxxxx", StringComparison.Ordinal), "a few characters of the name are kept");
        Check.Equal(cache.PathFor(K('a'), longName), cache.PathFor(K('a'), longName), "the same name every time");

        // it really can be written and finished
        string written = cache.BeginCopy(K('a'), longName);
        File.WriteAllBytes(written, new byte[3]);
        Check.True(cache.MarkFinished(K('a'), longName));
        Check.True(cache.TryGetReusable(K('a'), longName, out _));

        // a pair of UTF-16 surrogates (an emoji) is never cut in half
        string emoji = string.Concat(Enumerable.Repeat("\U0001F3AC", 130)) + ".mp4";
        string cut = Path.GetFileNameWithoutExtension(cache.PathFor(K('a'), emoji));
        Check.False(char.IsHighSurrogate(cut[^1]), "no half emoji at the end");
        Check.True(cache.PartPathFor(K('a'), emoji).Length <= 259);
    }

    [UnitTest]
    public static void Device_names_get_an_underscore()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string N(string original) => Path.GetFileName(cache.PathFor(K('a'), original));
        Check.Equal("CON_.mp4", N("CON.mkv"));
        Check.Equal("nul_.mp4", N("nul"));
        Check.Equal("Com1_.final.mp4", N("Com1.final.mkv"), "the part before the first dot counts");
        Check.Equal("LPT9_.mp4", N("LPT9 .mkv"));
        Check.Equal("aux_.mp4", N("aux.mp4"));
        Check.Equal("CONSOLE.mp4", N("CONSOLE.mkv"), "only the exact device names");
        Check.Equal("COM10.mp4", N("COM10.mkv"));

        string part = cache.BeginCopy(K('a'), "CON.mkv");
        File.WriteAllBytes(part, new byte[3]);
        Check.True(cache.MarkFinished(K('a'), "CON.mkv"), "a copy of a clip called CON can be written");
        Check.True(File.Exists(cache.PathFor(K('a'), "CON.mkv")));
    }

    [UnitTest]
    public static void A_finished_copy_is_reused_but_a_part_file_is_not()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string key = K('b');
        Check.False(cache.TryGetReusable(key, "a.mp4", out _), "nothing yet");
        string part = cache.BeginCopy(key, "a.mp4");
        File.WriteAllBytes(part, new byte[5]);
        Check.False(cache.TryGetReusable(key, "a.mp4", out _), "unfinished");
        Check.True(cache.MarkFinished(key, "a.mp4"));
        Check.False(File.Exists(part));
        Check.True(cache.TryGetReusable(key, "a.mp4", out string path));
        Check.Equal(cache.PathFor(key, "a.mp4"), path);

        File.WriteAllBytes(path, Array.Empty<byte>());
        Check.False(cache.TryGetReusable(key, "a.mp4", out _), "an empty file is no copy");
        Check.False(cache.MarkFinished(K('c'), "a.mp4"), "no part file to finish");
        File.WriteAllBytes(path, new byte[5]);
        File.WriteAllBytes(path + ".part", new byte[5]);
        Check.False(cache.TryGetReusable(key, "a.mp4", out _), "a leftover part file next to it means a copy is being made again");
    }

    [UnitTest]
    public static void MarkFinished_replaces_an_existing_copy_and_counts_as_a_use()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string final = Copy(cache, K('1'), 3, TimeSpan.FromDays(3), "a.mp4");
        string part = cache.BeginCopy(K('1'), "a.mp4");
        File.WriteAllBytes(part, new byte[7]);
        File.SetLastWriteTimeUtc(part, Now - TimeSpan.FromDays(2));
        Check.True(cache.MarkFinished(K('1'), "a.mp4"));
        Check.False(File.Exists(part));
        Check.Equal(7L, new FileInfo(final).Length, "the new copy replaced the old one");
        Check.Equal(Now, File.GetLastWriteTimeUtc(final), "finished now: a whole day before cleanup takes it");
        cache.Cleanup(Now);
        Check.True(File.Exists(final));
    }

    [UnitTest]
    public static void Cleanup_deletes_after_24_hours_not_at_the_boundary()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string oldCopy = Copy(cache, K('1'), 10, TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        string edge = Copy(cache, K('2'), 10, TimeSpan.FromHours(24));
        string fresh = Copy(cache, K('3'), 10, TimeSpan.FromHours(1));
        var r = cache.Cleanup(Now);
        Check.False(File.Exists(oldCopy));
        Check.False(Directory.Exists(Path.GetDirectoryName(oldCopy)!), "the empty key folder goes too");
        Check.True(File.Exists(edge), "exactly 24 h is kept");
        Check.True(File.Exists(fresh));
        Check.Equal(new ShareCache.CleanupResult(1, 10, 0), r);
        Check.True(Directory.Exists(cache.Root), "the root stays");
    }

    [UnitTest]
    public static void Cleanup_trims_oldest_first_over_the_cap()
    {
        using var dir = new TempDir();
        using var cache = Make(dir, maxBytes: 250);
        string oldest = Copy(cache, K('1'), 100, TimeSpan.FromHours(5));
        string middle = Copy(cache, K('2'), 100, TimeSpan.FromHours(3));
        string newest = Copy(cache, K('3'), 100, TimeSpan.FromHours(1));
        cache.Cleanup(Now);
        Check.False(File.Exists(oldest), "300 > 250: the oldest goes");
        Check.True(File.Exists(middle));
        Check.True(File.Exists(newest));

        using var cache2 = Make(dir, maxBytes: 50, name: "cache2");
        string a = Copy(cache2, K('4'), 100, TimeSpan.FromHours(2));
        string b = Copy(cache2, K('5'), 100, TimeSpan.FromHours(1));
        cache2.Cleanup(Now);
        Check.False(File.Exists(a));
        Check.False(File.Exists(b), "still over the cap: the next oldest goes as well");
    }

    [UnitTest]
    public static void In_use_entries_are_skipped_until_every_mark_is_disposed()
    {
        using var dir = new TempDir();
        using var cache = Make(dir, maxBytes: 110);
        string old = Copy(cache, K('1'), 100, TimeSpan.FromHours(48));
        string other = Copy(cache, K('2'), 100, TimeSpan.FromHours(2));
        string freshInUse = Copy(cache, K('3'), 10, TimeSpan.FromHours(1));
        var first = cache.MarkInUse(K('1'));
        var second = cache.MarkInUse(K('1'));
        using var third = cache.MarkInUse(K('3'));
        var r = cache.Cleanup(Now);
        Check.True(File.Exists(old), "in use: kept although it is old and over the cap");
        Check.False(File.Exists(other), "the cap takes the one that is not in use");
        Check.True(File.Exists(freshInUse));
        Check.Equal(1, r.EntriesSkipped, "only the in-use entry that was due counts as skipped");
        first.Dispose();
        first.Dispose();   // a second dispose releases nothing
        Check.True(cache.IsInUse(K('1')), "two marks, one released");
        cache.Cleanup(Now);
        Check.True(File.Exists(old));
        second.Dispose();
        Check.False(cache.IsInUse(K('1')));
        cache.Cleanup(Now);
        Check.False(File.Exists(old), "released: old, so it goes");
        Check.True(cache.IsInUse(K('3')), "another key's mark is untouched");
    }

    [UnitTest]
    public static void A_mark_taken_during_cleanup_keeps_the_entry()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string old = Copy(cache, K('1'), 10, Old);
        IDisposable? mark = null;
        cache.BeforeDeleteHook = key => mark ??= cache.MarkInUse(key);   // a Share window opens between measuring and deleting
        var r = cache.Cleanup(Now);
        Check.True(File.Exists(old), "marked after it was measured: still kept");
        Check.Equal(new ShareCache.CleanupResult(0, 0, 1), r);
        cache.BeforeDeleteHook = null;
        mark!.Dispose();
        cache.Cleanup(Now);
        Check.False(File.Exists(old));
    }

    [UnitTest]
    public static void An_entry_used_again_after_it_was_measured_is_kept()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string old = Copy(cache, K('1'), 10, Old);
        cache.BeforeDeleteHook = _ => File.SetLastWriteTimeUtc(old, Now);
        var r = cache.Cleanup(Now);
        Check.True(File.Exists(old), "its time is read again right before it would go");
        Check.Equal(default, r);
    }

    [UnitTest]
    public static void A_locked_file_is_skipped_and_cleaned_later()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string locked = Copy(cache, K('1'), 10, Old);
        string beside = Copy(cache, K('1'), 7, Old, "beside.mp4");
        string gone = Copy(cache, K('2'), 10, Old);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = cache.Cleanup(Now);
            Check.True(File.Exists(locked), "locked: skipped");
            Check.False(File.Exists(beside), "the rest of its folder goes");
            Check.False(File.Exists(gone));
            Check.Equal(new ShareCache.CleanupResult(1, 17, 1), r, "the bytes of a partly deleted entry count as freed");
        }
        cache.Cleanup(Now);
        Check.False(File.Exists(locked), "unlocked: removed by the next cleanup");
    }

    [UnitTest]
    public static void A_read_only_file_is_cleaned()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string ro = Copy(cache, K('1'), 10, Old);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var r = cache.Cleanup(Now);
        Check.False(File.Exists(ro));
        Check.False(Directory.Exists(Path.GetDirectoryName(ro)!));
        Check.Equal(new ShareCache.CleanupResult(1, 10, 0), r);
    }

    [UnitTest]
    public static void Folders_not_named_like_a_key_are_never_deleted()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        var foreign = new[] { "notakey", K('A'), new string('1', 31), new string('1', 33), new string('g', 32) }
            .Select(n => Path.Combine(cache.Root, n, "keep.mp4")).ToList();
        foreach (string f in foreign)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, new byte[10]);
            File.SetLastWriteTimeUtc(f, Now - TimeSpan.FromDays(30));
        }
        string ours = Copy(cache, K('2'), 10, Old);
        var r = cache.Cleanup(Now);
        foreach (string f in foreign) Check.True(File.Exists(f), "not a key folder: " + f);
        Check.False(File.Exists(ours));
        Check.Equal(new ShareCache.CleanupResult(1, 10, foreign.Count), r, "the other folders count as skipped");

        using var tiny = Make(dir, maxBytes: 0);
        tiny.Cleanup(Now);
        foreach (string f in foreign) Check.True(File.Exists(f), "the cap does not take them either: " + f);
    }

    [UnitTest]
    public static void A_junction_inside_the_cache_is_not_followed()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string target = dir.File("target");
        Directory.CreateDirectory(target);
        string precious = Path.Combine(target, "precious.mp4");
        File.WriteAllBytes(precious, new byte[10]);
        File.SetLastWriteTimeUtc(precious, Now - TimeSpan.FromDays(10));
        Directory.CreateDirectory(cache.Root);
        string link = Path.Combine(cache.Root, K('9'));
        string real = Copy(cache, K('1'), 10, Old);
        string inner = Path.Combine(Path.GetDirectoryName(real)!, "inner");
        try
        {
            // a junction as an entry, and one inside a real entry
            if (!MakeJunction(link, target) || !MakeJunction(inner, target)) { Check.Skip("a directory junction could not be created here (mklink /J)"); return; }

            cache.Cleanup(Now);
            Check.True(File.Exists(precious), "what a junction points to is never deleted");
            Check.True(Directory.Exists(link), "a junction entry is left alone");
            Check.False(File.Exists(real), "the real file went");
        }
        finally { RemoveJunction(inner); RemoveJunction(link); }
    }

    [UnitTest]
    public static void A_file_symlink_inside_an_entry_is_not_followed()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string precious = dir.File("precious.mp4");
        File.WriteAllBytes(precious, new byte[10]);
        File.SetLastWriteTimeUtc(precious, Now - TimeSpan.FromDays(10));
        string real = Copy(cache, K('1'), 10, Old);
        string link = Path.Combine(Path.GetDirectoryName(real)!, "link.mp4");
        try
        {
            // As in ArchiveTests (TryMakeSymbolicLink): making a symbolic link takes Developer Mode or an elevated process (CI is
            // elevated and makes it); without one the test says so and checks nothing more (the junction tests beside it need no
            // privilege). It prints and returns rather than Check.Skip, which would fail CLINK_TESTS_NO_SKIP=1 runs on a developer
            // machine that has neither.
            try { File.CreateSymbolicLink(link, precious); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("  (no symbolic link could be made here: Developer Mode or an elevated process is needed, so a file link inside an entry is not checked on this machine)");
                return;
            }

            var r = cache.Cleanup(Now);
            Check.True(File.Exists(precious), "what a link points to is never deleted");
            Check.Equal(10L, new FileInfo(precious).Length);
            Check.False(File.Exists(real), "the real file went");
            Check.True(new FileInfo(link).Exists, "the link is left: the entry is not ours to empty");
            Check.Equal(0, r.EntriesDeleted);
        }
        finally { try { File.Delete(link); } catch { } }
    }

    [UnitTest]
    public static void A_root_that_is_a_junction_is_not_cleaned()
    {
        using var dir = new TempDir();
        string target = dir.File("target");
        string keep = Path.Combine(target, K('1'), "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        File.WriteAllBytes(keep, new byte[10]);
        File.SetLastWriteTimeUtc(keep, Now - TimeSpan.FromDays(10));
        string root = dir.File("cache");
        try
        {
            if (!MakeJunction(root, target)) { Check.Skip("a directory junction could not be created here (mklink /J)"); return; }
            using var cache = Make(dir);
            Check.Equal(default, cache.Cleanup(Now));
            Check.True(File.Exists(keep), "a root that is a link is never cleaned");
        }
        finally { RemoveJunction(root); }
    }

    [UnitTest]
    public static void A_root_below_a_junction_is_not_cleaned()
    {
        using var dir = new TempDir();
        string real = dir.File("real");
        Directory.CreateDirectory(real);
        string link = dir.File("link");
        try
        {
            if (!MakeJunction(link, real)) { Check.Skip("a directory junction could not be created here (mklink /J)"); return; }
            using var cache = new ShareCache(Path.Combine(link, "cache"), () => Now, isElevated: () => false);
            string old = Copy(cache, K('1'), 10, Old);
            Check.True(File.Exists(Path.Combine(real, "cache", K('1'), "clip.mp4")), "the copy is really under the junction's target");
            Check.Equal(default, cache.Cleanup(Now));
            Check.True(File.Exists(old), "the root's final path is somewhere else: nothing is deleted");
        }
        finally { RemoveJunction(link); }
    }

    [UnitTest]
    public static void An_entry_swapped_for_a_junction_during_cleanup_is_left_alone()
    {
        using var dir = new TempDir();
        using var cache = Make(dir, maxBytes: 5);
        string target = dir.File("target");
        Directory.CreateDirectory(target);
        string precious = Path.Combine(target, "precious.mp4");
        File.WriteAllBytes(precious, new byte[10]);
        string first = Copy(cache, K('1'), 10, TimeSpan.FromHours(2));        // the whole entry becomes a junction
        string second = Copy(cache, K('2'), 10, TimeSpan.FromHours(1));       // a folder inside it becomes one
        string entry = Path.GetDirectoryName(first)!;
        string sub = Path.Combine(Path.GetDirectoryName(second)!, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "part.bin"), new byte[1]);
        File.SetLastWriteTimeUtc(Path.Combine(sub, "part.bin"), Now - TimeSpan.FromHours(1));
        bool made = true;
        cache.BeforeDeleteHook = key =>
        {
            if (key == K('1')) { Directory.Delete(entry, recursive: true); made &= MakeJunction(entry, target); }
            if (key == K('2')) { Directory.Delete(sub, recursive: true); made &= MakeJunction(sub, target); }
        };
        try
        {
            cache.Cleanup(Now);
            if (!made) { Check.Skip("a directory junction could not be created here (mklink /J)"); return; }
            Check.True(File.Exists(precious), "nothing behind a junction swapped in after measuring is deleted");
            Check.True(Directory.Exists(entry) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0, "the swapped entry is left as it is");
            Check.True(Directory.Exists(sub) && (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0, "the swapped folder is left as it is");
            Check.False(File.Exists(second), "the real file beside it went");
        }
        finally { RemoveJunction(sub); RemoveJunction(entry); }
    }

    [UnitTest]
    public static void An_elevated_process_does_not_clean()
    {
        using var dir = new TempDir();
        using var cache = new ShareCache(dir.File("cache"), () => Now, isElevated: () => true);
        string old = Copy(cache, K('1'), 10, Old);
        Check.Equal(default, cache.Cleanup(Now));
        Check.Equal(default, cache.Cleanup(Now), "logged once, skipped every time");
        Check.True(File.Exists(old), "elevated: nothing is deleted");

        Updater.IsElevatedOverride = true;
        Check.True(ShareCache.ProcessIsElevated(), "the default uses the updater's elevation test");
        Updater.IsElevatedOverride = false;
        Check.False(ShareCache.ProcessIsElevated());
        using var byDefault = new ShareCache(dir.File("cache"), () => Now);
        byDefault.Cleanup(Now);
        Check.False(File.Exists(old), "not elevated: cleaned");
    }

    [UnitTest]
    public static void An_elevated_process_never_writes_into_or_reuses_the_cache()
    {
        using var dir = new TempDir();
        using var plain = Make(dir);
        Check.True(plain.IsAvailable, "not elevated: available");
        string finished = Copy(plain, K('1'), 10, TimeSpan.FromHours(1));
        string part = plain.BeginCopy(K('3'), "a.mp4");
        File.WriteAllBytes(part, new byte[5]);

        using var cache = new ShareCache(dir.File("cache"), () => Now, isElevated: () => true);
        Check.False(cache.IsAvailable, "elevated: not available");
        Check.False(cache.TryGetReusable(K('1'), "clip.mp4", out _), "a finished copy is not reused");
        Check.Equal(Now - TimeSpan.FromHours(1), File.GetLastWriteTimeUtc(finished), "nor touched");
        var begin = Check.Throws<ShareCacheUnavailableException>(() => cache.BeginCopy(K('2'), "a.mp4"));
        Check.Equal(ShareCache.ElevatedReason, begin.Message);
        Check.False(Directory.Exists(Path.Combine(cache.Root, K('2'))), "no folder is made");
        var finish = Check.Throws<ShareCacheUnavailableException>(() => cache.MarkFinished(K('3'), "a.mp4"));
        Check.Equal(ShareCache.ElevatedReason, finish.Message);
        Check.True(File.Exists(part), "the part file is left as it is");
        Check.False(File.Exists(cache.PathFor(K('3'), "a.mp4")), "and not turned into a copy");
        Check.Throws<IOException>(() => cache.BeginCopy(K('2'), "a.mp4"), "a file error to a handler that does not know the type");
    }

    [UnitTest]
    public static void A_token_whose_elevation_is_not_0_counts_as_elevated()
    {
        Check.True(ShareCache.IsElevatedFrom(false, 1, 0x2000), "TokenElevation says elevated");
        Check.True(ShareCache.IsElevatedFrom(true, 0, 0x2000), "in the Administrators role");
        Check.True(ShareCache.IsElevatedFrom(false, 0, ShareCache.HighIntegrityRid), "High integrity");
        Check.True(ShareCache.IsElevatedFrom(false, 0, 0x4000), "System integrity");
        Check.True(ShareCache.IsElevatedFrom(null, 0, 0x2000), "the role unknown");
        Check.True(ShareCache.IsElevatedFrom(false, null, 0x2000), "TokenElevation unknown");
        Check.True(ShareCache.IsElevatedFrom(false, 0, null), "the integrity level unknown");
        Check.False(ShareCache.IsElevatedFrom(false, 0, 0x2000), "a plain Medium integrity token");
        Check.False(ShareCache.IsElevatedFrom(false, 0, 0x1000), "Low integrity");

        var real = ShareCache.ReadToken();
        Check.True(real.InAdministratorRole.HasValue && real.TokenElevation.HasValue && real.IntegrityRid.HasValue, $"Windows says all three here: {real}");
        Updater.IsElevatedOverride = null;   // the real test (the runner restores the override after the test)
        try
        {
            Check.Equal(ShareCache.IsElevatedFrom(real.InAdministratorRole, real.TokenElevation, real.IntegrityRid), ShareCache.ProcessIsElevated());
            ShareCache.TokenElevationOverride = () => 1;
            Check.True(ShareCache.ProcessIsElevated(), "a token whose TokenElevation is not 0 is elevated, whatever the role says");
            using var dir = new TempDir();
            using var cache = new ShareCache(dir.File("cache"), () => Now);
            Check.False(cache.IsAvailable, "the default elevation test is used");
            ShareCache.TokenElevationOverride = () => null;
            Check.True(ShareCache.ProcessIsElevated(), "a TokenElevation Windows will not give counts as elevated");
        }
        finally { ShareCache.TokenElevationOverride = null; }
    }

    [UnitTest]
    public static void A_key_folder_that_is_a_junction_is_never_written_or_reused()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string target = dir.File("target");
        Directory.CreateDirectory(target);
        string behind = Path.Combine(target, "clip.mp4");
        File.WriteAllBytes(behind, new byte[10]);
        Directory.CreateDirectory(cache.Root);
        string link = Path.Combine(cache.Root, K('1'));
        try
        {
            if (!MakeJunction(link, target)) { Check.Skip("a directory junction could not be created here (mklink /J)"); return; }
            Check.False(cache.TryGetReusable(K('1'), "clip.mp4", out _), "a copy behind a junction is not reused");
            var ex = Check.Throws<ShareCacheUnavailableException>(() => cache.BeginCopy(K('1'), "clip.mp4"), "BeginCopy refuses a key folder that is a junction");
            Check.Contains(ex.Message, "junction");
            string partBehind = Path.Combine(target, "clip.mp4.part");
            File.WriteAllBytes(partBehind, new byte[7]);
            Check.Throws<ShareCacheUnavailableException>(() => cache.MarkFinished(K('1'), "clip.mp4"), "MarkFinished refuses it too");
            Check.Equal(10L, new FileInfo(behind).Length, "nothing behind the junction is replaced");
            Check.True(File.Exists(partBehind));
        }
        finally { RemoveJunction(link); }
    }

    [UnitTest]
    public static void BeginCopy_refuses_a_root_below_a_junction()
    {
        using var dir = new TempDir();
        string real = dir.File("real");
        Directory.CreateDirectory(real);
        string link = dir.File("link");
        try
        {
            if (!MakeJunction(link, real)) { Check.Skip("a directory junction could not be created here (mklink /J)"); return; }
            using var cache = new ShareCache(Path.Combine(link, "cache"), () => Now, isElevated: () => false);
            Check.True(cache.IsAvailable);
            Check.Throws<ShareCacheUnavailableException>(() => cache.BeginCopy(K('1'), "a.mp4"));
            Check.False(Directory.Exists(Path.Combine(real, "cache")), "nothing is made behind the junction");
        }
        finally { RemoveJunction(link); }
    }

    [UnitTest]
    public static void Names_ending_with_a_dot_or_a_space_are_never_touched()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string plain = Copy(cache, K('1'), 10, Old);   // clip.mp4
        string entry = @"\\?\" + Path.GetDirectoryName(plain)!;
        // Made through the \\?\ form, which keeps the dot or space: the normal form of "clip.mp4." is clip.mp4 (another file).
        string dotFile = entry + @"\clip.mp4.";
        string spaceFile = entry + @"\other.mp4 ";
        string dotDir = entry + @"\sub.";
        string inner = dotDir + @"\inner.mp4";
        Directory.CreateDirectory(dotDir);
        foreach (string f in new[] { dotFile, spaceFile, inner })
        {
            File.WriteAllBytes(f, new byte[3]);
            File.SetLastWriteTimeUtc(f, Now - Old);
        }
        try
        {
            var r = cache.Cleanup(Now);
            Check.True(File.Exists(dotFile), "a file whose name ends with a dot is left");
            Check.True(File.Exists(spaceFile), "a file whose name ends with a space is left");
            Check.True(File.Exists(inner), "a folder whose name ends with a dot is not entered");
            Check.False(File.Exists(plain), "the plain file beside them went");
            Check.Equal(0, r.EntriesDeleted, "the entry is not gone");
            Check.Equal(1, r.EntriesSkipped);
        }
        finally
        {
            foreach (string f in new[] { dotFile, spaceFile, inner }) try { File.Delete(f); } catch { }
            try { Directory.Delete(dotDir); } catch { }
        }
    }

    [UnitTest]
    public static void A_mark_for_another_key_does_not_wait_for_a_delete()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string a1 = Copy(cache, K('a'), 10, Old, "a1.mp4");
        string a2 = Copy(cache, K('a'), 10, Old, "a2.mp4");
        string b = Copy(cache, K('b'), 10, TimeSpan.FromHours(1));
        using var entered = new ManualResetEventSlim();
        int hookDone = 0;
        // The first delete in key a's folder holds on until key a is marked (at most 5 s), so the marks below are taken while it is deleted.
        cache.BeforeEachDeleteHook = path =>
        {
            if (entered.IsSet || !path.Contains(K('a'), StringComparison.Ordinal)) return;
            entered.Set();
            var wait = Stopwatch.StartNew();
            while (!cache.IsInUse(K('a')) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(5);
            Volatile.Write(ref hookDone, 1);
        };
        var run = Task.Run(() => cache.Cleanup(Now));
        Check.True(entered.Wait(5000), "cleanup started deleting key a");
        Check.True(cache.IsBeingDeleted(K('a')));
        var sw = Stopwatch.StartNew();
        using (cache.MarkInUse(K('b'))) { }
        long ms = sw.ElapsedMilliseconds;
        Check.True(ms < 100, $"a mark for key b does not wait for key a's delete ({ms} ms)");
        Check.Equal(0, Volatile.Read(ref hookDone), "key a was still being deleted meanwhile");

        using var markA = cache.MarkInUse(K('a'));
        Check.Equal(1, Volatile.Read(ref hookDone), "a mark for key a returns only once the delete in progress is over");
        Check.False(cache.IsBeingDeleted(K('a')));
        Check.True(run.Wait(10000), "cleanup ended");
        Check.True(File.Exists(a1) && File.Exists(a2), "marked before its first file went: the delete stopped, nothing of it is gone");
        Check.True(File.Exists(b));
        Check.Equal(0, run.Result.EntriesDeleted);
    }

    [UnitTest]
    public static void A_read_only_file_with_another_hard_link_keeps_its_attribute()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string outside = dir.File("outside.mp4");
        string outsideToo = dir.File("outside-too.mp4");
        File.WriteAllBytes(outside, new byte[10]);
        File.WriteAllBytes(outsideToo, new byte[10]);
        string inside = cache.PathFor(K('1'), "clip.mp4");
        string insideToo = cache.PathFor(K('2'), "clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        Directory.CreateDirectory(Path.GetDirectoryName(insideToo)!);
        if (!CreateHardLink(inside, outside, 0) || !CreateHardLink(insideToo, outsideToo, 0))
        {
            Console.WriteLine($"  (no hard link could be made here (error {Marshal.GetLastWin32Error()}), so a hard-linked file in the cache is not checked on this machine)");
            return;
        }
        File.SetLastWriteTimeUtc(outside, Now - Old);
        File.SetLastWriteTimeUtc(outsideToo, Now - Old);
        File.SetAttributes(outside, FileAttributes.ReadOnly);   // the attribute belongs to the file, so every link of it
        try
        {
            var r = cache.Cleanup(Now);
            Check.True(File.Exists(outside), "the file outside the cache is still there");
            Check.True((File.GetAttributes(outside) & FileAttributes.ReadOnly) != 0, "and still read-only");
            Check.True(File.Exists(inside), "the read-only link in the cache is left");
            Check.False(File.Exists(insideToo), "a link that is not read-only is deleted: only the link goes");
            Check.Equal(10L, new FileInfo(outsideToo).Length, "the file behind it is intact");
            Check.Equal(1, r.EntriesDeleted);
            Check.Equal(1, r.EntriesSkipped);
        }
        finally { try { File.SetAttributes(outside, FileAttributes.Normal); } catch { } }
    }

    [UnitTest]
    public static void A_root_given_with_8dot3_names_is_still_used_and_cleaned()
    {
        using var dir = new TempDir();
        string longDir = dir.File("A folder with a long name");
        Directory.CreateDirectory(longDir);
        var buffer = new char[1024];
        uint n = GetShortPathName(longDir, buffer, (uint)buffer.Length);
        string shortDir = n > 0 && n < buffer.Length ? new string(buffer, 0, (int)n) : longDir;
        if (string.Equals(shortDir, longDir, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  (8.3 names are off on this volume, so a root given with them is not checked on this machine)");
            return;
        }
        using var cache = new ShareCache(Path.Combine(shortDir, "cache"), () => Now, isElevated: () => false);
        string part = cache.BeginCopy(K('1'), "a.mp4");
        File.WriteAllBytes(part, new byte[5]);
        Check.True(cache.MarkFinished(K('1'), "a.mp4"));
        Check.True(cache.TryGetReusable(K('1'), "a.mp4", out string copy), "an 8.3 root is the root: its copies are reused");
        File.SetLastWriteTimeUtc(copy, Now - Old);
        var r = cache.Cleanup(Now);
        Check.Equal(1, r.EntriesDeleted, "and cleaned");
        Check.False(File.Exists(Path.Combine(longDir, "cache", K('1'), "a.mp4")));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newLink, string existing, nint security);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, [Out] char[] shortPath, uint length);

    [UnitTest]
    public static void Only_one_cleanup_runs_at_a_time()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string old = Copy(cache, K('1'), 10, Old);
        ShareCache.CleanupResult? nested = null;
        bool wasCleaning = false;
        cache.BeforeDeleteHook = _ => { wasCleaning = cache.IsCleaning; nested = cache.Cleanup(Now); };
        var r = cache.Cleanup(Now);
        Check.True(wasCleaning);
        Check.Equal(default(ShareCache.CleanupResult), nested!.Value, "a second cleanup while one runs returns at once");
        Check.Equal(1, r.EntriesDeleted, "the running one goes on");
        Check.False(File.Exists(old));
        Check.False(cache.IsCleaning);
    }

    [UnitTest]
    public static void Empty_folders_go_after_a_grace_time()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string fresh = Path.Combine(cache.Root, K('1'));
        string stale = Path.Combine(cache.Root, K('2'));
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(stale);
        Directory.SetLastWriteTimeUtc(fresh, Now - TimeSpan.FromMinutes(1));
        Directory.SetLastWriteTimeUtc(stale, Now - TimeSpan.FromHours(1));
        cache.Cleanup(Now);
        Check.True(Directory.Exists(fresh), "a folder just made may be about to get its copy");
        Check.False(Directory.Exists(stale));
    }

    [UnitTest]
    public static void Cleanup_without_a_root_does_nothing()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        Check.Equal(default, cache.Cleanup(Now));
        Check.False(Directory.Exists(cache.Root), "cleaning does not create the root");
    }

    [UnitTest]
    public static void Root_guard_refuses_empty_relative_and_drive_roots()
    {
        Check.Throws<ArgumentException>(() => new ShareCache(""));
        Check.Throws<ArgumentException>(() => new ShareCache("   "));
        Check.Throws<ArgumentException>(() => new ShareCache(@"share-cache"));
        Check.Throws<ArgumentException>(() => new ShareCache(@"C:\"));
        Check.Throws<ArgumentException>(() => new ShareCache(@"C:"));
        Check.Throws<ArgumentException>(() => new ShareCache(@"\\server\share\"));
        using var ok = new ShareCache(@"C:\Temp\share-cache\");
        Check.Equal(@"C:\Temp\share-cache", ok.Root);
    }

    [UnitTest]
    public static void Default_root_is_inside_the_dev_folder_under_dev()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        Check.Equal(Path.Combine(dir.Path, "share-cache"), ShareCache.DefaultRoot());
        ShareCache.ResetCurrentForTests();
        Check.Equal(Path.Combine(dir.Path, "share-cache"), ShareCache.Current.Root, "Current is made with the --dev root");
        Check.True(ReferenceEquals(ShareCache.Current, ShareCache.Current), "one instance");
        ShareCache.DisposeCurrent();
        Settings.DirOverride = null;
        string real = ShareCache.DefaultRoot();
        Check.True(real.EndsWith(Path.Combine("Clink", "share-cache"), StringComparison.OrdinalIgnoreCase));
        Check.False(real.StartsWith(dir.Path, StringComparison.OrdinalIgnoreCase));
        ShareCache.DisposeCurrent();   // nothing was made since: nothing happens
    }

    [UnitTest]
    public static void The_schedule_runs_cleanup_off_the_calling_thread()
    {
        using var dir = new TempDir();
        using var cache = Make(dir);
        string old = Copy(cache, K('1'), 10, Old);
        cache.StartSchedule(TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1));
        var sw = Stopwatch.StartNew();
        while (File.Exists(old) && sw.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        Check.False(File.Exists(old), "the scheduled cleanup ran");
    }

    [UnitTest]
    public static void Dispose_stops_the_schedule()
    {
        using var dir = new TempDir();
        var cache = Make(dir);
        string old = Copy(cache, K('1'), 10, Old);
        cache.StartSchedule(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
        cache.Dispose();
        Thread.Sleep(900);
        Check.True(File.Exists(old), "disposed before the first run: never cleaned");
        Check.Throws<ObjectDisposedException>(() => cache.StartSchedule());
        cache.Dispose();   // twice is harmless
    }

    [UnitTest]
    public static void Dispose_waits_for_a_running_cleanup()
    {
        using var dir = new TempDir();
        var cache = Make(dir);
        string a = Copy(cache, K('1'), 10, Old);
        string b = Copy(cache, K('2'), 10, Old);
        using var entered = new ManualResetEventSlim();
        cache.BeforeDeleteHook = _ => { entered.Set(); Thread.Sleep(1000); };
        cache.StartSchedule(TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1));
        Check.True(entered.Wait(5000), "the scheduled cleanup started");
        var sw = Stopwatch.StartNew();
        cache.Dispose();
        Check.False(cache.IsCleaning, "Dispose returned only once the cleanup was over");
        Check.True(sw.ElapsedMilliseconds < 3000, $"and within its wait ({sw.ElapsedMilliseconds} ms)");
        Check.True(File.Exists(a) && File.Exists(b), "a cleanup disposed under it stops before deleting more");
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return Directory.Exists(link) && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Removes a junction (the link only, never its target); nothing when it is not one.</summary>
    private static void RemoveJunction(string link)
    {
        try
        {
            var info = new DirectoryInfo(link);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) info.Delete();
        }
        catch { }
    }
}
