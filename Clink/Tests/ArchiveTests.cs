using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Archive: which clips a run picks (<see cref="ArchiveService.SelectCandidates"/>, pure), manual runs ("Archive now…") end to
/// end on temp folders, the status lines, and the words the dialog uses.
/// </summary>
internal static class ArchiveTests
{
    private const string Folder = @"C:\Clips";
    private const long GB = 1L << 30;
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0);

    // ------------------------------------------------------------------ which clips (pure)

    private static ClipEntry Clip(string name, double ageDays, string folder = Folder, bool archived = false) => new()
    {
        Path = System.IO.Path.Combine(folder, name),
        Created = Now.AddDays(-ageDays),
        ArchivedAt = archived ? Now : null,
    };

    private static string[] Names(IEnumerable<ClipEntry> clips) => clips.Select(c => c.FileName).ToArray();

    private static List<ClipEntry> Pick(ClipEntry[] entries, ArchiveScope scope, int days, double gb, long localBytes, Func<ClipEntry, long>? sizeOf = null) =>
        ArchiveService.SelectCandidates(entries, Folder, scope, days, gb, localBytes, Now, sizeOf ?? (_ => GB));

    [UnitTest]
    public static void Under_the_GB_limit_with_young_clips_All_takes_every_clip_oldest_first_and_Due_takes_none()
    {
        var entries = new[] { Clip("b.mp4", 0.2), Clip("a.mp4", 0.5), Clip("c.mp4", 0.1) };   // 3 GB of 100, all under a day old
        Check.Equal(new[] { "a.mp4", "b.mp4", "c.mp4" }, Names(Pick(entries, ArchiveScope.All, 1, 100, 3 * GB)));
        Check.Equal(0, Pick(entries, ArchiveScope.Due, 1, 100, 3 * GB).Count, "due by the rules");
        Check.Equal(0, Pick(entries, ArchiveScope.Due, 0, 0, 3 * GB).Count, "no rule picks nothing");
        Check.Equal(3, Pick(entries, ArchiveScope.All, 0, 0, 3 * GB).Count, "All does not need a rule");
    }

    [UnitTest]
    public static void Archived_clips_and_clips_outside_the_clips_folder_are_never_picked()
    {
        var entries = new[]
        {
            Clip("local.mp4", 5),
            Clip("archived.mp4", 9, @"T:\clips", archived: true),
            Clip("archived-here.mp4", 9, Folder, archived: true),
            Clip("elsewhere.mp4", 9, @"D:\Other"),
            Clip("deeper.mp4", 9, Folder + @"\sub"),
            new ClipEntry { Path = "", Created = Now.AddDays(-9) },
        };
        foreach (var scope in new[] { ArchiveScope.All, ArchiveScope.Due })
            Check.Equal(new[] { "local.mp4" }, Names(Pick(entries, scope, 1, 0, GB)), scope.ToString());

        // The folder may be spelled with a trailing backslash or in another case.
        var spelled = ArchiveService.SelectCandidates(entries, @"c:\CLIPS\", ArchiveScope.All, 0, 0, GB, Now, _ => GB);
        Check.Equal(new[] { "local.mp4" }, Names(spelled), "another spelling of the folder");
    }

    [UnitTest]
    public static void Due_by_size_stops_once_the_folder_is_under_the_limit()
    {
        var entries = new[] { Clip("d.mp4", 2), Clip("a.mp4", 5), Clip("e.mp4", 1), Clip("b.mp4", 4), Clip("c.mp4", 3) };   // five 1 GB clips, 5 GB in the folder
        Check.Equal(new[] { "a.mp4", "b.mp4", "c.mp4" }, Names(Pick(entries, ArchiveScope.Due, 0, 2.5, 5 * GB)), "5 GB over 2.5: three go, 2 GB are left");
        Check.Equal(new[] { "a.mp4" }, Names(Pick(entries, ArchiveScope.Due, 0, 4, 5 * GB)), "5 GB over 4: one goes");
        Check.Equal(0, Pick(entries, ArchiveScope.Due, 0, 5, 5 * GB).Count, "exactly at the limit is not over it");
        Check.Equal(0, Pick(entries, ArchiveScope.Due, 0, 100, 5 * GB).Count, "under the limit");
        // Big clips count by their own size: one 3 GB clip is enough.
        var sizes = new Dictionary<string, long> { ["a.mp4"] = 3 * GB };
        Check.Equal(new[] { "a.mp4" }, Names(Pick(entries, ArchiveScope.Due, 0, 2.5, 5 * GB, e => sizes.GetValueOrDefault(e.FileName, GB))), "a big oldest clip brings the folder under at once");
    }

    [UnitTest]
    public static void Due_by_age_takes_the_clips_older_than_the_cutoff_oldest_first()
    {
        var entries = new[] { Clip("young.mp4", 1), Clip("old.mp4", 5), Clip("older.mp4", 3), Clip("edge.mp4", 2), Clip("mid.mp4", 2.5) };
        Check.Equal(new[] { "old.mp4", "older.mp4", "mid.mp4" }, Names(Pick(entries, ArchiveScope.Due, 2, 0, 5 * GB)), "older than 2 days (exactly 2 days is not older)");
        Check.Equal(new[] { "old.mp4", "older.mp4", "mid.mp4", "edge.mp4" }, Names(Pick(entries, ArchiveScope.Due, 1, 0, 5 * GB)), "older than 1 day");
    }

    [UnitTest]
    public static void Due_takes_a_clip_for_its_age_or_for_the_folders_size()
    {
        var entries = new[] { Clip("old.mp4", 10), Clip("day.mp4", 1), Clip("new.mp4", 0.5) };
        // 3 GB in the folder, limit 1.5 GB: the old one goes for its age (2 GB left), the day-old one for the size (1 GB left), the newest stays.
        Check.Equal(new[] { "old.mp4", "day.mp4" }, Names(Pick(entries, ArchiveScope.Due, 3, 1.5, 3 * GB)));
        // Under the limit only age counts.
        Check.Equal(new[] { "old.mp4" }, Names(Pick(entries, ArchiveScope.Due, 3, 100, 3 * GB)));
    }

    [UnitTest]
    public static void A_clip_whose_file_is_gone_is_skipped_and_does_not_shrink_the_folder()
    {
        var entries = new[] { Clip("gone.mp4", 9), Clip("a.mp4", 8), Clip("b.mp4", 7) };
        long Size(ClipEntry e) => e.FileName == "gone.mp4" ? -1 : GB;
        // Two 1 GB clips in the folder, limit 1.5: only the older of them goes (the missing file is not part of the 2 GB).
        Check.Equal(new[] { "a.mp4" }, Names(Pick(entries, ArchiveScope.Due, 0, 1.5, 2 * GB, Size)));
        Check.Equal(new[] { "a.mp4", "b.mp4" }, Names(Pick(entries, ArchiveScope.All, 0, 0, 2 * GB, Size)));
    }

    [UnitTest]
    public static void Odd_rule_values_do_not_throw()
    {
        var entries = new[] { Clip("a.mp4", 5) };
        Check.Equal(0, Pick(entries, ArchiveScope.Due, int.MaxValue, 0, GB).Count, "int.MaxValue days");
        Check.Equal(0, Pick(entries, ArchiveScope.Due, 0, double.MaxValue, GB).Count, "a huge GB limit");
        Check.Equal(0, Pick(entries, ArchiveScope.Due, 0, double.NaN, GB).Count, "NaN GB");
        Check.Equal(0, Pick(entries, ArchiveScope.Due, -3, -1, GB).Count, "negative rules are off");
        Check.Equal(1, Pick(entries, ArchiveScope.All, int.MaxValue, double.NaN, GB).Count, "All ignores the rules");
    }

    // ------------------------------------------------------------------ a manual run, end to end (temp folders)

    /// <summary>A clips folder, an archive folder and a service over them, with the settings an "Archive now…" needs and nothing more.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly TempDir _meta = new();
        public readonly TempDir Clips = new(), Archive = new();
        public readonly Settings Settings;
        public readonly ClipLibrary Library = new();
        public readonly ArchiveService Service;
        private readonly Random _random = new(7);

        public Rig()
        {
            ClipLibrary.IndexPathOverride = System.IO.Path.Combine(_meta.Path, "clips.json");   // this rig's own index
            Settings = new Settings { ClipsFolder = Clips.Path, ArchivePath = Archive.Path, ArchiveEnabled = false };
            Service = new ArchiveService(Settings, Library);
        }

        /// <summary>A clip in the clips folder: random bytes, created <paramref name="ageDays"/> ago, last written <paramref name="writtenMinutesAgo"/> ago.</summary>
        public byte[] Add(string name, int kilobytes, double ageDays, double writtenMinutesAgo = 60)
        {
            var bytes = new byte[kilobytes * 1024];
            _random.NextBytes(bytes);
            string path = Clips.File(name);
            File.WriteAllBytes(path, bytes);
            File.SetCreationTime(path, DateTime.Now.AddDays(-ageDays));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-writtenMinutesAgo));
            WaitUntilNobodyHasItOpen(path);
            return bytes;
        }

        /// <summary>A big clip without the cost of its bytes: <paramref name="megabytes"/> of zeros (the file system makes them when they are read).</summary>
        public void AddBig(string name, int megabytes, double ageDays)
        {
            string path = Clips.File(name);
            using (var fs = File.Create(path)) fs.SetLength((long)megabytes << 20);
            File.SetCreationTime(path, DateTime.Now.AddDays(-ageDays));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-60));
            WaitUntilNobodyHasItOpen(path);
        }

        public void Load() => Library.Load(Clips.Path);

        public ArchiveRunResult Run(ArchiveScope scope, bool throttle = false, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
            Finish(Service.RunManualAsync(scope, throttle, progress, ct));

        /// <summary>A run whose pace is asked for per chunk.</summary>
        public ArchiveRunResult RunPaced(ArchiveScope scope, Func<bool> throttle, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default) =>
            Finish(Service.RunManualAsync(scope, throttle, progress, ct));

        private static ArchiveRunResult Finish(Task<ArchiveRunResult> task)
        {
            if (!task.Wait(TimeSpan.FromSeconds(60))) throw new TestFailure("the manual run did not finish within 60 s");
            return task.Result;
        }

        public ClipEntry Entry(string name) => Library.Entries.First(e => e.FileName == name);

        public void Dispose()
        {
            Service.Stop();
            Clips.Dispose(); Archive.Dispose(); _meta.Dispose();
        }
    }

    /// <summary>A virus scanner may still be reading a file that was just written, and the service skips a file that is open: let it finish first.</summary>
    private static void WaitUntilNobodyHasItOpen(string path)
    {
        for (int i = 0; i < 100; i++)
        {
            try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return; }
            catch (IOException) { Thread.Sleep(20); }
        }
    }

    /// <summary>Collects progress on the spot (a Progress&lt;T&gt; would post to the UI thread, which a test blocks).</summary>
    private sealed class Sink : IProgress<ArchiveProgress>
    {
        public readonly List<ArchiveProgress> Seen = new();
        public Action<ArchiveProgress>? OnReport;
        public void Report(ArchiveProgress value) { lock (Seen) Seen.Add(value); OnReport?.Invoke(value); }
    }

    [UnitTest]
    public static void A_manual_run_moves_every_local_clip_whatever_the_rules_and_the_switch()
    {
        using var rig = new Rig();   // the automatic switch is off and there is no rule
        var bytes = new Dictionary<string, byte[]>
        {
            ["newest.mp4"] = rig.Add("newest.mp4", 300, 0.1),
            ["oldest.mp4"] = rig.Add("oldest.mp4", 700, 0.9),
            ["middle.mp4"] = rig.Add("middle.mp4", 500, 0.4),
        };
        rig.Load();
        var sink = new Sink();
        var r = rig.Run(ArchiveScope.All, throttle: false, sink);

        Check.Equal(3, r.Eligible, "eligible"); Check.Equal(3, r.Moved, "moved");
        Check.Equal(0, r.InUse, "in use"); Check.Equal(0, r.TooNew, "too new"); Check.Equal(0, r.Failed, "failed");
        Check.Equal(1500L * 1024, r.BytesMoved, "bytes moved");
        Check.Null(r.Problem, "problem"); Check.False(r.Cancelled, "cancelled");
        foreach (var (name, content) in bytes)
        {
            Check.True(File.Exists(rig.Archive.File(name)), name + " is in the archive");
            Check.True(File.ReadAllBytes(rig.Archive.File(name)).AsSpan().SequenceEqual(content), name + " arrived intact");
            Check.False(File.Exists(rig.Clips.File(name)), name + " is gone from the clips folder");
            var e = rig.Entry(name);
            Check.True(e.IsArchived, name + " is archived in the library");
            Check.Equal(rig.Archive.File(name), e.Path, name + " points at the archive");
        }
        Check.Equal(0, Directory.GetFiles(rig.Archive.Path, "*.clinkpart").Length, "no partial copy left");

        // Oldest first, and every clip reported from 0 to 100 % as "i of 3".
        var order = sink.Seen.Select(p => p.File).Distinct().ToArray();
        Check.Equal(new[] { "oldest.mp4", "middle.mp4", "newest.mp4" }, order, "order of the moves");
        Check.True(sink.Seen.All(p => p.Count == 3), "the count is the number of clips picked");
        Check.Equal(new[] { 1, 2, 3 }, sink.Seen.Select(p => p.Index).Distinct().ToArray(), "1 of 3, 2 of 3, 3 of 3");
        foreach (var name in order)
        {
            var one = sink.Seen.Where(p => p.File == name).Select(p => p.Percent).ToList();
            Check.Equal(0, one.First(), name + " starts at 0 %"); Check.Equal(100, one.Last(), name + " ends at 100 %");
            Check.True(one.SequenceEqual(one.OrderBy(p => p)), name + " only goes forward");
        }
        Check.Equal("Automatic moves off · Archive now… moves clips by hand", rig.Service.Status);
    }

    [UnitTest]
    public static void A_manual_Due_run_moves_what_the_rules_pick_and_leaves_the_rest()
    {
        using var rig = new Rig();
        rig.Settings.ArchiveAfterDays = 2;   // the switch stays off: the rules are still the user's idea of "due"
        rig.Add("old.mp4", 200, 5);
        rig.Add("young.mp4", 200, 0.5);
        rig.Load();
        var r = rig.Run(ArchiveScope.Due);
        Check.Equal(1, r.Eligible); Check.Equal(1, r.Moved);
        Check.True(File.Exists(rig.Archive.File("old.mp4")), "the old clip moved");
        Check.True(File.Exists(rig.Clips.File("young.mp4")), "the young clip stayed");
        Check.False(rig.Entry("young.mp4").IsArchived, "the young clip is still local in the library");

        // Nothing is due any more: an honest zero, not a failure.
        var again = rig.Run(ArchiveScope.Due);
        Check.Equal(0, again.Eligible); Check.Equal(0, again.Moved); Check.Null(again.Problem);
    }

    [UnitTest]
    public static void A_manual_run_leaves_and_counts_clips_that_are_open_or_too_new()
    {
        using var rig = new Rig();
        rig.Add("fine.mp4", 100, 3);
        rig.Add("open.mp4", 100, 4);
        rig.Add("fresh.mp4", 100, 1, writtenMinutesAgo: 5.0 / 60);   // written 5 s ago: the muxer may still be on it
        rig.Load();
        ArchiveRunResult first;
        using (new FileStream(rig.Clips.File("open.mp4"), FileMode.Open, FileAccess.Read, FileShare.None))   // somebody has it open
            first = rig.Run(ArchiveScope.All);
        Check.Equal(3, first.Eligible, "eligible"); Check.Equal(1, first.Moved, "moved");
        Check.Equal(1, first.InUse, "in use"); Check.Equal(1, first.TooNew, "too new"); Check.Equal(0, first.Failed, "failed");
        Check.True(File.Exists(rig.Archive.File("fine.mp4")), "the free clip moved");
        Check.True(File.Exists(rig.Clips.File("open.mp4")) && !rig.Entry("open.mp4").IsArchived, "the open clip stayed where it was");
        Check.True(File.Exists(rig.Clips.File("fresh.mp4")) && !rig.Entry("fresh.mp4").IsArchived, "the fresh clip stayed where it was");

        // "Try again later": once nothing has it open the next run takes it; the fresh one still has to wait.
        var second = rig.Run(ArchiveScope.All);
        Check.Equal(1, second.Moved, "the retry moved the clip that was open"); Check.Equal(0, second.InUse, "in use now"); Check.Equal(1, second.TooNew, "still too new");
        Check.True(rig.Entry("open.mp4").IsArchived, "open.mp4 is archived now");
    }

    [UnitTest]
    public static void Cancelling_mid_copy_deletes_the_partial_copy_and_keeps_the_local_clip()
    {
        using var rig = new Rig();
        var bigBytes = rig.Add("big.mp4", 8 * 1024, 2);   // 16 chunks: the copy is under way for a while
        rig.Add("next.mp4", 100, 1);
        rig.Load();
        var cts = new CancellationTokenSource();
        var sink = new Sink { OnReport = p => { if (p.Percent >= 25) cts.Cancel(); } };   // the user presses Cancel a quarter of the way in
        var r = rig.Run(ArchiveScope.All, throttle: false, sink, cts.Token);

        Check.True(r.Cancelled, "cancelled"); Check.Equal(0, r.Moved, "nothing moved");
        Check.Equal(0, Directory.GetFiles(rig.Archive.Path).Length, "nothing left in the archive, not even a .clinkpart");
        Check.True(File.ReadAllBytes(rig.Clips.File("big.mp4")).AsSpan().SequenceEqual(bigBytes), "the local clip is untouched");
        Check.True(File.Exists(rig.Clips.File("next.mp4")), "the next clip was never started");
        Check.False(rig.Entry("big.mp4").IsArchived, "the library still has it local");
        Check.True(sink.Seen.Max(p => p.Percent) < 100, "it stopped before the end");

        // The service is not left stuck: the same clips move in a later run.
        var again = rig.Run(ArchiveScope.All);
        Check.Equal(2, again.Moved, "a fresh run moves both");
    }

    [UnitTest]
    public static void A_run_whose_token_is_already_cancelled_does_nothing()
    {
        using var rig = new Rig();
        rig.Add("a.mp4", 100, 2);
        rig.Load();
        var r = rig.Run(ArchiveScope.All, ct: new CancellationToken(canceled: true));
        Check.True(r.Cancelled, "cancelled"); Check.Equal(0, r.Moved, "moved");
        Check.True(File.Exists(rig.Clips.File("a.mp4")), "the clip is where it was");
        Check.Equal(0, Directory.GetFiles(rig.Archive.Path).Length, "the archive is empty");
    }

    [UnitTest]
    public static void A_manual_run_is_refused_without_a_usable_location_and_moves_nothing()
    {
        using var rig = new Rig();
        rig.Add("a.mp4", 100, 2);
        rig.Load();

        rig.Settings.ArchivePath = null;
        var none = rig.Run(ArchiveScope.All);
        Check.Equal("no archive location is set", none.Problem, "no location"); Check.Equal(0, none.Moved);

        rig.Settings.ArchivePath = @"nas\clips";   // a lost backslash: relative to wherever the exe runs
        var relative = rig.Run(ArchiveScope.All);
        Check.Contains(relative.Problem, "not a full path", "relative location"); Check.Equal(0, relative.Moved);

        rig.Settings.ArchivePath = rig.Clips.Path + @"\";   // "move" a clip onto itself would delete it
        var same = rig.Run(ArchiveScope.All);
        Check.Contains(same.Problem, "clips folder", "the clips folder itself"); Check.Equal(0, same.Moved);

        Check.True(File.Exists(rig.Clips.File("a.mp4")), "the clip is where it was");
        Check.Equal(0, Directory.GetFiles(rig.Archive.Path).Length, "nothing was written to the archive");
        foreach (var refused in new[] { none, relative, same }) Check.Equal(0, refused.Eligible, "a refusal looks at no clip");
    }

    [UnitTest]
    public static void Preview_counts_what_each_choice_would_move()
    {
        using var rig = new Rig();
        rig.Settings.ArchiveAfterDays = 2;
        rig.Add("old.mp4", 400, 5);
        rig.Add("young.mp4", 100, 0.5);
        rig.Load();
        var p = rig.Service.Preview();
        Check.Equal(1, p.DueCount, "due"); Check.Equal(400L * 1024, p.DueBytes, "due bytes");
        Check.Equal(2, p.AllCount, "all"); Check.Equal(500L * 1024, p.AllBytes, "all bytes");

        rig.Settings.ArchiveAfterDays = 0;   // no rule: nothing is due, everything can still be moved
        var none = rig.Service.Preview();
        Check.Equal(0, none.DueCount, "due without a rule"); Check.Equal(2, none.AllCount, "all without a rule");
    }

    // ------------------------------------------------------------------ the status line

    [UnitTest]
    public static void The_idle_status_says_what_the_automatic_switch_and_the_location_allow()
    {
        using var rig = new Rig();
        var s = rig.Settings;
        var svc = rig.Service;

        s.ArchivePath = null; s.ArchiveEnabled = false; svc.Reevaluate();
        Check.Equal("Archive off", svc.Status, "nothing set");

        s.ArchivePath = Directory.GetCurrentDirectory(); s.ArchiveEnabled = false; svc.Reevaluate();
        Check.Equal("Automatic moves off · Archive now… moves clips by hand", svc.Status, "a location, switch off");

        s.ArchivePath = null; s.ArchiveEnabled = true; svc.Reevaluate();
        Check.Contains(svc.Status, "no archive location", "switch on, no location");

        s.ArchivePath = rig.Archive.Path; s.ArchiveEnabled = true; s.ArchiveAfterDays = 0; s.ArchiveWhenLocalExceedsGB = 0; svc.Reevaluate();
        Check.Contains(svc.Status, "no rule", "switch on, no rule");   // the Storage card turns this line amber on "no rule"
    }

    // ------------------------------------------------------------------ the dialog's words

    [UnitTest]
    public static void The_dialog_words_counts_rules_progress_and_results()
    {
        // Sizes are formatted in the user's culture ("3,4 GB" on a German PC); the expected text is the English one.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { DialogWords(); }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    private static void DialogWords()
    {
        Check.Equal("0 clips", ArchiveWindow.CountText(0, 0));
        Check.Equal("1 clip · 120 MB", ArchiveWindow.CountText(1, 120L << 20));
        Check.Equal("12 clips · 3.4 GB", ArchiveWindow.CountText(12, (long)(3.4 * GB)));

        Check.Equal("older than 1 day / while the folder is over 100 GB", ArchiveWindow.RuleText(1, 100));
        Check.Equal("older than 30 days", ArchiveWindow.RuleText(30, 0));
        Check.Equal("while the folder is over 0.5 GB", ArchiveWindow.RuleText(0, 0.5));
        Check.Equal("no rule set", ArchiveWindow.RuleText(0, 0));

        Check.Equal("Moving 3 of 12 · Copying clip.mp4 (40 %)", ArchiveWindow.ProgressText(new ArchiveProgress(3, 12, "clip.mp4", 40)));

        long gb34 = (long)(3.4 * GB);
        Check.Equal(@"Moved 12 clips (3.4 GB) to T:\clips · 1 in use, try again later",
            ArchiveWindow.ResultText(new ArchiveRunResult(13, 12, 1, 0, 0, gb34, null, false), @"T:\clips"));
        Check.Equal("Moved 1 clip (3.4 GB) to T:\\clips", ArchiveWindow.ResultText(new ArchiveRunResult(1, 1, 0, 0, 0, gb34, null, false), @"T:\clips"));
        Check.Equal("Nothing to move", ArchiveWindow.ResultText(new ArchiveRunResult(0, 0, 0, 0, 0, 0, null, false), @"T:\clips"));
        Check.Equal("Archive unreachable", ArchiveWindow.ResultText(new ArchiveRunResult(0, 0, 0, 0, 0, 0, "archive unreachable", false), @"T:\clips"));
        Check.Equal("Nothing was moved · 2 saved less than 30 s ago, try again later", ArchiveWindow.ResultText(new ArchiveRunResult(2, 0, 0, 2, 0, 0, null, false), @"T:\clips"));
        Check.Equal(@"Moved 2 clips (3.4 GB) to T:\clips · 3 failed: archive is full (1 MB free)",
            ArchiveWindow.ResultText(new ArchiveRunResult(5, 2, 0, 0, 3, gb34, "archive is full (1 MB free)", false), @"T:\clips"));
        Check.Equal("Cancelled, nothing was moved", ArchiveWindow.ResultText(new ArchiveRunResult(4, 0, 0, 0, 0, 0, null, true), @"T:\clips"));
        Check.Equal(@"Cancelled after moving 3 clips (3.4 GB) to T:\clips", ArchiveWindow.ResultText(new ArchiveRunResult(9, 3, 0, 0, 0, gb34, null, true), @"T:\clips"));
    }

    // ==================================================================== review fixes
    // A local clip is deleted only when a different, complete copy exists; the automatic size rule replaces a clip that does not
    // move; the pace follows the game; a failure is not reported as "in use".

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TestFailure("timed out waiting for " + what);
            Thread.Sleep(5);
        }
    }

    /// <summary>A junction (a second name for a folder, no privilege needed) made with the system's own mklink.</summary>
    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    private static string[] FileNames(string folder) =>
        Directory.GetFiles(folder).Select(f => System.IO.Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // ------------------------------------------------------------------ 1. never delete a clip that is its own "archive copy"

    [UnitTest]
    public static void A_destination_that_is_the_clips_folder_under_another_name_moves_nothing_and_loses_nothing()
    {
        using var rig = new Rig();
        var a = rig.Add("a.mp4", 200, 3);
        var b = rig.Add("b.mp4", 300, 2);
        rig.Load();
        using var links = new TempDir();
        string junction = links.File("clips-link");
        Check.True(TryMakeJunction(junction, rig.Clips.Path), "mklink /J made no junction");
        try
        {
            // Both spellings pass the check by name (they differ from the clips folder's), so the file system has to say.
            foreach (var (label, alias) in new[] { (@"\\?\ spelling", @"\\?\" + rig.Clips.Path), ("junction", junction) })
            {
                rig.Settings.ArchivePath = alias;
                var r = rig.Run(ArchiveScope.All);
                Check.Equal(0, r.Moved, label + ": moved");
                Check.NotNull(r.Problem, label + ": the run says why");
                Check.Contains(r.Problem, "clips folder", label);
                Check.True(File.ReadAllBytes(rig.Clips.File("a.mp4")).AsSpan().SequenceEqual(a), label + ": a.mp4 is intact");
                Check.True(File.ReadAllBytes(rig.Clips.File("b.mp4")).AsSpan().SequenceEqual(b), label + ": b.mp4 is intact");
                Check.Equal(new[] { "a.mp4", "b.mp4" }, FileNames(rig.Clips.Path), label + ": nothing added to or removed from the clips folder (no probe left)");
                Check.False(rig.Entry("a.mp4").IsArchived, label + ": the library still has it local");
            }
        }
        finally { Directory.Delete(junction); }   // the link only, not the clips folder behind it
    }

    [UnitTest]
    public static void The_alias_probe_sees_a_second_name_for_a_folder_and_leaves_nothing_behind()
    {
        using var one = new TempDir();
        using var two = new TempDir();
        using var links = new TempDir();
        string junction = links.File("one-link");
        Check.True(TryMakeJunction(junction, one.Path), "mklink /J made no junction");
        try
        {
            Check.Null(ArchiveService.AliasProblem(two.Path, one.Path), "two different folders");
            Check.NotNull(ArchiveService.AliasProblem(junction, one.Path), "a junction to the clips folder");
            Check.NotNull(ArchiveService.AliasProblem(one.Path, junction), "the other way round");
            Check.NotNull(ArchiveService.AliasProblem(@"\\?\" + one.Path, one.Path), @"the \\?\ spelling");
            Check.Null(ArchiveService.AliasProblem(System.IO.Path.Combine(two.Path, "does-not-exist"), one.Path), "a folder that is not there says nothing (nothing can be copied there either)");
            Check.Equal(0, Directory.GetFiles(one.Path).Length + Directory.GetFiles(two.Path).Length, "no probe file left behind");
        }
        finally { Directory.Delete(junction); }
    }

    [UnitTest]
    public static void The_Test_button_refuses_another_name_for_the_clips_folder()
    {
        using var rig = new Rig();
        using var links = new TempDir();
        string junction = links.File("clips-link");
        Check.True(TryMakeJunction(junction, rig.Clips.Path), "mklink /J made no junction");
        try
        {
            var (ok, message, _) = rig.Service.TestAsync(junction).GetAwaiter().GetResult();
            Check.False(ok, "a junction to the clips folder");
            Check.Contains(message, "another name");
            var (fine, text, _) = rig.Service.TestAsync(rig.Archive.Path).GetAwaiter().GetResult();
            Check.True(fine, "a different folder passes: " + text);
        }
        finally { Directory.Delete(junction); }
    }

    /// <summary>A clip and an archive copy of it in one folder, its last write time put well in the past so that it is stable.</summary>
    private static (string Clip, string Copy, byte[] Bytes, DateTime Written) GuardFiles(TempDir dir)
    {
        string clip = dir.File("clip.mp4"), copy = dir.File("copy.mp4");
        var bytes = RandomBytes(100 * 1024, 5);
        File.WriteAllBytes(clip, bytes);
        File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllBytes(copy, bytes);
        WaitUntilNobodyHasItOpen(clip);
        WaitUntilNobodyHasItOpen(copy);
        return (clip, copy, bytes, File.GetLastWriteTimeUtc(clip));
    }

    [UnitTest]
    public static void The_delete_guard_deletes_a_clip_only_while_a_distinct_complete_copy_is_open()
    {
        using var dir = new TempDir();
        var (clip, copy, bytes, written) = GuardFiles(dir);
        using (var guard = ArchiveService.DeleteGuard.Open(clip, copy, bytes.Length, written, out var result, out var problem))
        {
            Check.NotNull(guard, "a guard (" + result + ": " + problem + ")");
            Check.Equal(ArchiveService.GuardResult.Ready, result);
            Check.True(guard!.Delete(out _, out _), "the delete through the handle");
            Check.True(File.Exists(copy), "the copy is untouched by the delete");
        }   // closing the guard is what finishes the delete
        Check.False(File.Exists(clip), "the clip is gone");
        Check.True(File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes), "the copy is intact");
    }

    [UnitTest]
    public static void The_delete_guard_cannot_delete_a_clip_that_is_its_own_archive_copy()
    {
        using var dir = new TempDir();
        using var links = new TempDir();
        var (clip, _, bytes, written) = GuardFiles(dir);
        string junction = links.File("dir-link");
        Check.True(TryMakeJunction(junction, dir.Path), "mklink /J made no junction");
        try
        {
            // The same file under another name: what a share of the clips folder looks like when its volume serial is not the
            // local one and the name and the file index check have already been fooled. The guard is the last line.
            foreach (var (label, aliasCopy) in new[] { ("same path", clip), ("junction", System.IO.Path.Combine(junction, "clip.mp4")) })
            {
                using var guard = ArchiveService.DeleteGuard.Open(clip, aliasCopy, bytes.Length, written, out var result, out _);
                Check.Null(guard, label + ": no guard for a clip that is its own copy");
                Check.True(result is ArchiveService.GuardResult.InUse or ArchiveService.GuardResult.SameFile, label + ": refused (" + result + ")");
                Check.True(File.ReadAllBytes(clip).AsSpan().SequenceEqual(bytes), label + ": the clip is intact");
            }
        }
        finally { Directory.Delete(junction); }
    }

    [UnitTest]
    public static void The_delete_guard_refuses_when_the_copy_is_missing_or_short_or_the_clip_is_not_what_was_copied()
    {
        using var dir = new TempDir();
        var (clip, copy, bytes, written) = GuardFiles(dir);
        int length = bytes.Length;

        File.Move(copy, copy + ".away");
        using (var none = ArchiveService.DeleteGuard.Open(clip, copy, length, written, out var missing, out _))
        { Check.Null(none); Check.Equal(ArchiveService.GuardResult.CopyMissing, missing, "no copy"); }
        File.Move(copy + ".away", copy);

        using (var fs = new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.None)) fs.SetLength(length - 1);
        using (var shortCopy = ArchiveService.DeleteGuard.Open(clip, copy, length, written, out var differs, out var why))
        { Check.Null(shortCopy); Check.Equal(ArchiveService.GuardResult.CopyDiffers, differs, "a copy one byte short"); Check.Contains(why, "bytes"); }
        File.WriteAllBytes(copy, bytes);

        using (var older = ArchiveService.DeleteGuard.Open(clip, copy, length, written.AddSeconds(1), out var changedTime, out _))
        { Check.Null(older); Check.Equal(ArchiveService.GuardResult.Changed, changedTime, "the clip was written at another time than the one copied"); }

        using (var fs = new FileStream(clip, FileMode.Append, FileAccess.Write, FileShare.None)) fs.WriteByte(1);
        File.SetLastWriteTimeUtc(clip, written);
        using (var longer = ArchiveService.DeleteGuard.Open(clip, copy, length, written, out var changedLength, out _))
        { Check.Null(longer); Check.Equal(ArchiveService.GuardResult.Changed, changedLength, "the clip grew"); }

        Check.True(File.Exists(clip) && File.Exists(copy), "nothing was deleted");
    }

    [UnitTest]
    public static void The_delete_guard_leaves_a_clip_that_something_has_open_or_that_may_not_be_deleted()
    {
        using var dir = new TempDir();
        var (clip, copy, bytes, written) = GuardFiles(dir);

        using (var writer = new FileStream(clip, FileMode.Open, FileAccess.Write, FileShare.Read))   // an uploader, a muxer
        using (var busy = ArchiveService.DeleteGuard.Open(clip, copy, bytes.Length, written, out var writing, out _))
        { Check.Null(busy); Check.Equal(ArchiveService.GuardResult.InUse, writing, "open for writing"); }

        using (var player = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read))   // a player that does not share delete
        using (var busy = ArchiveService.DeleteGuard.Open(clip, copy, bytes.Length, written, out var playing, out _))
        { Check.Null(busy); Check.Equal(ArchiveService.GuardResult.InUse, playing, "open by a player that does not let it be deleted"); }

        File.SetAttributes(clip, FileAttributes.ReadOnly);
        try
        {
            using var guard = ArchiveService.DeleteGuard.Open(clip, copy, bytes.Length, written, out var result, out var problem);
            if (guard != null) Check.False(guard.Delete(out result, out problem), "a read-only clip cannot be deleted");
            Check.Equal(ArchiveService.GuardResult.Denied, result, "read-only");
            Check.Contains(problem, "access denied");
        }
        finally { File.SetAttributes(clip, FileAttributes.Normal); }
        Check.True(File.Exists(clip), "the clip is still there");
    }

    [UnitTest]
    public static void A_clip_that_a_trim_replaced_in_the_meantime_is_never_deleted_by_its_name()
    {
        using var dir = new TempDir();
        var (clip, copy, bytes, written) = GuardFiles(dir);
        var cut = RandomBytes(40 * 1024, 77);
        string cutFile = dir.File("cut.mp4");
        File.WriteAllBytes(cutFile, cut);
        WaitUntilNobodyHasItOpen(cutFile);
        using (var guard = ArchiveService.DeleteGuard.Open(clip, copy, bytes.Length, written, out _, out _))
        {
            Check.NotNull(guard, "a guard");
            // A new cut takes the clip's name while the clip is being deleted ("Replace original"). Renaming it over the clip is
            // held off while the guard has the clip open (NTFS: access denied), but moving the clip aside and putting the cut in
            // its place is not, and a delete by name would then take the cut.
            bool overwritten;
            try { File.Move(cutFile, clip, overwrite: true); overwritten = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { overwritten = false; }
            if (!overwritten) { File.Move(clip, clip + ".old"); File.Move(cutFile, clip); }
            guard!.Delete(out _, out _);   // whatever this returns, it concerns the file that was checked, not whatever has the name now
            guard.Dispose();
        }
        Check.True(File.Exists(clip) && File.ReadAllBytes(clip).AsSpan().SequenceEqual(cut), "the cut that took the clip's name survived the delete");
        Check.False(File.Exists(clip + ".old"), "the file the guard had checked is the one that went");
        Check.True(File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes), "the archive copy is intact");
    }

    // ------------------------------------------------------------------ 2. the automatic size rule, and the automatic pass in general

    [UnitTest]
    public static void The_automatic_size_rule_takes_the_next_clip_when_the_oldest_is_open()
    {
        using var rig = new Rig();
        var s = rig.Settings;
        s.ArchiveEnabled = true; s.ArchiveAfterDays = 0; s.ArchiveWhenLocalExceedsGB = 0.0004;   // 0.0004 GB = 429,496 bytes
        rig.Add("a-oldest.mp4", 200, 5);   // held open below
        rig.Add("b.mp4", 100, 4);
        rig.Add("c.mp4", 100, 3);
        rig.Add("d.mp4", 100, 2);          // 500 KB in the folder: over the limit by about 80 KB, which the oldest clip alone would cover
        rig.Load();
        using (new FileStream(rig.Clips.File("a-oldest.mp4"), FileMode.Open, FileAccess.Read, FileShare.None))
            rig.Service.RunAutomaticOnce();
        Check.True(File.Exists(rig.Clips.File("a-oldest.mp4")), "the open clip stayed");
        Check.True(File.Exists(rig.Archive.File("b.mp4")) && !File.Exists(rig.Clips.File("b.mp4")), "the next clip took its place");
        Check.True(rig.Entry("b.mp4").IsArchived, "and the library knows");
        Check.True(File.Exists(rig.Clips.File("c.mp4")) && File.Exists(rig.Clips.File("d.mp4")), "the folder is under the limit after that: no more");
        rig.Service.RunAutomaticOnce();   // nothing has the oldest open now, but the folder is under the limit
        Check.True(File.Exists(rig.Clips.File("a-oldest.mp4")), "under the limit nothing is due");
    }

    [UnitTest]
    public static void The_automatic_pass_moves_what_is_due_and_nothing_while_the_switch_is_off()
    {
        using var rig = new Rig();
        rig.Settings.ArchiveAfterDays = 2;
        rig.Add("old.mp4", 100, 5);
        rig.Add("young.mp4", 100, 0.5);
        rig.Load();
        rig.Service.RunAutomaticOnce();   // the switch is off
        Check.True(File.Exists(rig.Clips.File("old.mp4")), "switch off: nothing moves");
        rig.Settings.ArchiveEnabled = true;
        rig.Service.RunAutomaticOnce();
        Check.True(File.Exists(rig.Archive.File("old.mp4")) && !File.Exists(rig.Clips.File("old.mp4")), "the old clip moved");
        Check.True(File.Exists(rig.Clips.File("young.mp4")), "the young clip stayed");
        Check.Contains(rig.Service.Status, "1 clip archived");
    }

    [UnitTest]
    public static void The_automatic_pass_gives_way_to_a_manual_run_and_runs_again_soon_after()
    {
        using var rig = new Rig();
        rig.Settings.ArchiveEnabled = true;
        rig.Settings.ArchiveAfterDays = 1;   // the big old clip is due, the small young one is not
        rig.AddBig("big.mp4", 32, 5);        // at the automatic pass's gentle pace (8 ms per 512 KB) that is about a second of copying
        rig.Add("small.mp4", 100, 0.2);
        rig.Load();
        rig.Service.ScheduleAutomaticNow();   // the archive thread begins an automatic pass now
        WaitFor(() => Directory.GetFiles(rig.Archive.Path, "*.clinkpart").Length > 0, "the automatic pass to start copying big.mp4");

        var r = rig.Run(ArchiveScope.All);    // the user presses Archive now… in the middle of it
        Check.Equal(2, r.Moved, "the manual run moved both itself: the automatic pass gave way in the middle of big.mp4");
        Check.Equal(0, Directory.GetFiles(rig.Archive.Path, "*.clinkpart").Length, "no partial copy left");
        Check.True(File.Exists(rig.Archive.File("big.mp4")) && File.Exists(rig.Archive.File("small.mp4")), "both are in the archive");
        var next = rig.Service.NextAutomaticPassUtc - DateTime.UtcNow;
        Check.True(next > TimeSpan.Zero && next < TimeSpan.FromSeconds(90), $"the automatic pass was put back to within about a minute (it is due in {next.TotalSeconds:0} s, not the 5 min of a normal period)");
    }

    // ------------------------------------------------------------------ 3. the pace follows the game

    [UnitTest]
    public static void The_pace_is_asked_for_every_chunk_and_a_throwing_answer_does_not_stop_the_run()
    {
        using var rig = new Rig();
        rig.AddBig("big.mp4", 4, 2);   // 8 chunks of 512 KB
        rig.Add("small.mp4", 100, 1);
        rig.Load();
        int asked = 0;
        var r = rig.RunPaced(ArchiveScope.All, () => { Interlocked.Increment(ref asked); return false; });
        Check.Equal(2, r.Moved, "moved");
        Check.True(asked >= 9, $"the pace was asked for {asked} times: once for the log and once per chunk (8 for big.mp4), not decided once");

        rig.Settings.ArchivePath = rig.Archive.Path;
        rig.AddBig("big2.mp4", 4, 2);
        rig.Load();
        var again = rig.RunPaced(ArchiveScope.All, () => throw new InvalidOperationException("no answer"));
        Check.Equal(1, again.Moved, "an answer that throws counts as 'gentle' and the run goes on");
    }

    // ------------------------------------------------------------------ 5. what is not "in use"

    [UnitTest]
    public static void A_clip_that_changes_while_it_is_copied_is_left_to_try_again_later()
    {
        using var rig = new Rig();
        var bytes = rig.Add("a.mp4", 4096, 2);   // 8 chunks
        rig.Load();
        string local = rig.Clips.File("a.mp4");
        bool touched = false;
        var sink = new Sink
        {
            OnReport = p =>
            {
                if (p.Percent < 25 || touched) return;
                touched = true;
                File.SetLastWriteTimeUtc(local, DateTime.UtcNow.AddMinutes(-5));   // somebody wrote to it (attribute changes do not need the file's data to be free)
            },
        };
        var r = rig.Run(ArchiveScope.All, false, sink);
        Check.True(touched, "the clip was touched during the copy");
        Check.Equal(1, r.InUse, "in use"); Check.Equal(0, r.Failed, "failed"); Check.Equal(0, r.Moved, "moved"); Check.Null(r.Problem, "problem");
        Check.True(File.ReadAllBytes(local).AsSpan().SequenceEqual(bytes), "the clip is intact");
        Check.Equal(0, Directory.GetFiles(rig.Archive.Path).Length, "the stale copy is gone, partial or not");

        var later = rig.Run(ArchiveScope.All);   // nothing writes to it any more
        Check.Equal(1, later.Moved, "a later run moves it");
    }

    [UnitTest]
    public static void A_short_copy_is_a_failure_and_a_clip_that_changed_is_a_retry()
    {
        var t = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Check.Equal(ArchiveService.CopyCheck.Good, ArchiveService.CheckCopy(100, 100, 100, t, t));
        Check.Equal(ArchiveService.CopyCheck.Short, ArchiveService.CheckCopy(99, 100, 100, t, t), "the copy is short, the clip is as it was");
        Check.Equal(ArchiveService.CopyCheck.SourceChanged, ArchiveService.CheckCopy(100, 100, 101, t, t), "the clip grew");
        Check.Equal(ArchiveService.CopyCheck.SourceChanged, ArchiveService.CheckCopy(100, 100, 100, t.AddSeconds(2), t), "the clip was written to");
        Check.Equal(ArchiveService.CopyCheck.SourceChanged, ArchiveService.CheckCopy(99, 100, 101, t, t), "a clip that changed explains a short copy: retry");
    }

    [UnitTest]
    public static void A_clip_that_cannot_be_deleted_is_a_failure_with_its_reason_and_the_next_run_finishes_it()
    {
        using var rig = new Rig();
        var bytes = rig.Add("a.mp4", 300, 2);
        string local = rig.Clips.File("a.mp4");
        File.SetAttributes(local, FileAttributes.ReadOnly);
        rig.Load();
        try
        {
            var r = rig.Run(ArchiveScope.All);
            Check.Equal(0, r.Moved, "moved"); Check.Equal(0, r.InUse, "not 'in use'"); Check.Equal(1, r.Failed, "failed");
            Check.Contains(r.Problem, "cannot delete the local clip");
            Check.True(File.Exists(local), "the clip stayed");
            Check.True(File.ReadAllBytes(rig.Archive.File("a.mp4")).AsSpan().SequenceEqual(bytes), "the verified copy is kept for the next run");
            Check.False(rig.Library.Entries.Single().IsArchived, "the library still has it local");
        }
        finally { File.SetAttributes(local, FileAttributes.Normal); }
        var again = rig.Run(ArchiveScope.All);
        Check.Equal(1, again.Moved, "with the permission fixed the next run adopts the copy and finishes the move");
        Check.False(File.Exists(local), "the local clip is gone now");
        Check.True(rig.Library.Entries.Single().IsArchived, "archived");
    }

    [UnitTest]
    public static void A_clip_that_may_not_be_read_is_a_failure_not_in_use()
    {
        using var rig = new Rig();
        rig.Add("a.mp4", 100, 2);
        string local = rig.Clips.File("a.mp4");
        rig.Load();
        var info = new FileInfo(local);
        var original = info.GetAccessControl();
        var denyRead = new FileSecurity();
        denyRead.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(denyRead);
        try
        {
            var r = rig.Run(ArchiveScope.All);
            Check.Equal(0, r.Moved, "moved"); Check.Equal(0, r.InUse, "not 'in use'"); Check.Equal(1, r.Failed, "failed");
            Check.Contains(r.Problem, "access denied");
            Check.Equal(0, Directory.GetFiles(rig.Archive.Path).Length, "nothing was copied");
        }
        finally { info.SetAccessControl(original); }
        Check.True(File.Exists(local), "the clip is still there");
    }

    [UnitTest]
    public static void A_clip_that_is_a_symbolic_link_is_left_alone_not_deleted_through()
    {
        // The link test itself, on a junction (which needs no privilege; a symbolic link goes through the same check).
        using (var folder = new TempDir())
        using (var links = new TempDir())
        {
            string junction = links.File("dir-link");
            Check.True(TryMakeJunction(junction, folder.Path), "mklink /J made no junction");
            try { Check.True(ArchiveService.IsLink(new FileInfo(junction)), "a junction is a link"); }
            finally { Directory.Delete(junction); }
            string plain = folder.File("plain.mp4");
            File.WriteAllBytes(plain, new byte[10]);
            Check.False(ArchiveService.IsLink(new FileInfo(plain)), "a plain file is not");
        }

        // A clip that is a symbolic link, when this process may make one (Developer Mode or elevated): left alone. The link was made a
        // moment ago, so its own timestamps are new, whatever the file behind it says: it is refused as a link, not counted as too new.
        using var rig = new Rig();
        using var elsewhere = new TempDir();
        string target = elsewhere.File("real.mp4");
        var bytes = RandomBytes(100 * 1024, 11);
        File.WriteAllBytes(target, bytes);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddMinutes(-60));
        string link = rig.Clips.File("linked.mp4");
        if (!TryMakeSymbolicLink(link, target)) return;
        File.SetCreationTime(link, DateTime.Now.AddDays(-2));
        rig.Load();
        var r = rig.Run(ArchiveScope.All);
        Check.Equal(0, r.Moved, "moved");
        Check.Equal(1, r.Failed, "failed: a link is not archived");
        Check.Equal(0, r.TooNew, "and not counted as too new either, though the link itself is new");
        Check.Contains(r.Problem, "link");
        Check.True(File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes), "the file behind the link is intact");
        Check.True(File.Exists(link), "and so is the link");
    }

    /// <summary>
    /// A symbolic link at <paramref name="link"/> to <paramref name="target"/>, or false when this process may not make one (that takes
    /// Developer Mode or an elevated process): then the test says so on its way out, so that a run that checked nothing does not look as
    /// if it had. CI is elevated, so it makes the links.
    /// </summary>
    private static bool TryMakeSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string why = ex.Message;
            int path = why.IndexOf(" : '", StringComparison.Ordinal);   // the message names the file, which says nothing here
            if (path > 0) why = why[..path];
            Console.WriteLine($"  (no symbolic link could be made here: {why.TrimEnd('.', ' ')}. Making one takes Developer Mode or an elevated process, so the run over a symbolic link is not checked on this machine; the junction test beside it is, and so is any machine that can make links, as the CI runner can)");
            return false;
        }
    }

    [UnitTest]
    public static void A_back_dated_link_is_refused_too()
    {
        using var rig = new Rig();
        using var elsewhere = new TempDir();
        string target = elsewhere.File("real.mp4");
        var bytes = RandomBytes(100 * 1024, 12);
        File.WriteAllBytes(target, bytes);
        string link = rig.Clips.File("linked.mp4");
        if (!TryMakeSymbolicLink(link, target)) return;
        File.SetLastWriteTimeUtc(link, DateTime.UtcNow.AddMinutes(-60));   // the link's own time: old enough for the age rule
        rig.Load();
        var r = rig.Run(ArchiveScope.All);
        Check.Equal(0, r.Moved, "moved"); Check.Equal(1, r.Failed, "failed: a link is not archived, however old"); Check.Equal(0, r.TooNew, "too new");
        Check.Contains(r.Problem, "link");
        Check.True(File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes), "the file behind the link is intact");
        Check.True(File.Exists(link), "and so is the link");
    }

    [UnitTest]
    public static void A_link_is_refused_before_its_age_is_looked_at_a_junction_shows_it_on_every_machine()
    {
        // A junction needs no privilege, and a run looks at it as at a symbolic link: its attributes and timestamps are its own.
        using var folder = new TempDir();
        using var links = new TempDir();
        string junction = links.File("clip-link");
        Check.True(TryMakeJunction(junction, folder.Path), "mklink /J made no junction");
        try
        {
            var now = DateTime.UtcNow;
            var fresh = new FileInfo(junction);
            Check.True(now - fresh.LastWriteTimeUtc < TimeSpan.FromSeconds(30), "a link made a moment ago looks new: the age rule alone would call it too new");
            Check.Equal(ArchiveService.FileVerdict.Link, ArchiveService.Judge(fresh, now), "but a link is refused as a link first");
            Directory.SetLastWriteTimeUtc(junction, now.AddMinutes(-60));   // the junction's own time, as for a symbolic link
            Check.Equal(ArchiveService.FileVerdict.Link, ArchiveService.Judge(new FileInfo(junction), now), "and whatever its age");

            // A file is judged by its age: just written is too new, written an hour ago is for moving.
            string plain = folder.File("plain.mp4");
            File.WriteAllBytes(plain, new byte[10]);
            Check.Equal(ArchiveService.FileVerdict.TooNew, ArchiveService.Judge(new FileInfo(plain), DateTime.UtcNow), "a file written a moment ago");
            File.SetLastWriteTimeUtc(plain, now.AddMinutes(-60));
            Check.Equal(ArchiveService.FileVerdict.Movable, ArchiveService.Judge(new FileInfo(plain), now), "a file written an hour ago");
        }
        finally { Directory.Delete(junction); }
    }

    // ------------------------------------------------------------------ 6. the copy left by an earlier run

    [UnitTest]
    public static void A_copy_left_by_an_earlier_run_is_adopted_and_only_the_delete_is_left_to_do()
    {
        using var rig = new Rig();
        var bytes = rig.Add("a.mp4", 300, 2);
        File.WriteAllBytes(rig.Archive.File("a.mp4"), bytes);   // what a run leaves behind when its local delete failed
        WaitUntilNobodyHasItOpen(rig.Archive.File("a.mp4"));
        rig.Load();
        var r = rig.Run(ArchiveScope.All);
        Check.Equal(1, r.Moved, "moved"); Check.Equal(0, r.Failed, "failed");
        Check.False(File.Exists(rig.Clips.File("a.mp4")), "the local clip is gone");
        Check.True(File.ReadAllBytes(rig.Archive.File("a.mp4")).AsSpan().SequenceEqual(bytes), "the copy is as it was");
        Check.Equal(1, Directory.GetFiles(rig.Archive.Path).Length, "no second copy was made");
        var entry = rig.Library.Entries.Single();
        Check.True(entry.IsArchived, "archived"); Check.Equal(rig.Archive.File("a.mp4"), entry.Path);
    }

    [UnitTest]
    public static void A_different_file_with_the_same_name_in_the_archive_is_never_overwritten()
    {
        using var rig = new Rig();
        var bytes = rig.Add("a.mp4", 300, 2);
        var other = RandomBytes(bytes.Length, 99);
        File.WriteAllBytes(rig.Archive.File("a.mp4"), other);   // same name, same length, other content
        WaitUntilNobodyHasItOpen(rig.Archive.File("a.mp4"));
        rig.Load();
        var r = rig.Run(ArchiveScope.All);
        Check.Equal(1, r.Moved, "moved");
        Check.True(File.ReadAllBytes(rig.Archive.File("a.mp4")).AsSpan().SequenceEqual(other), "the file that was there is untouched");
        Check.True(File.ReadAllBytes(rig.Archive.File("a (2).mp4")).AsSpan().SequenceEqual(bytes), "the clip arrived under a new name");
        Check.Equal(rig.Archive.File("a (2).mp4"), rig.Library.Entries.Single().Path);
    }

    // ------------------------------------------------------------------ 7. the library entry follows the delete, under the library's lock

    [UnitTest]
    public static void The_library_follows_the_entry_it_holds_now_and_deletes_nothing_for_a_clip_that_left()
    {
        using var meta = new TempDir();
        using var clips = new TempDir();
        using var archive = new TempDir();
        ClipLibrary.IndexPathOverride = meta.File("clips.json");
        string src = clips.File("a.mp4"), trim = clips.File("a-trim.mp4"), dest = archive.File("a.mp4");
        File.WriteAllBytes(src, new byte[100]);
        File.WriteAllBytes(trim, new byte[50]);
        File.WriteAllBytes(dest, new byte[100]);
        var lib = new ClipLibrary();
        lib.Add(src, 1, 100, null, Array.Empty<string>());
        lib.Add(trim, 1, 50, null, Array.Empty<string>(), isTrim: true, sourcePath: src);
        var snapshot = lib.Find(src)!;
        lib.Load(clips.Path);   // a reload (the clips folder was changed, Clink restarted) replaces the entries by new objects
        Check.False(ReferenceEquals(snapshot, lib.Find(src)), "the reload made new entries");

        int deletes = 0;
        var moved = lib.MoveToArchive(snapshot, src, dest, () => { deletes++; File.Delete(src); return true; });
        Check.Equal(ArchiveMove.Moved, moved); Check.Equal(1, deletes, "the delete ran once");
        var live = lib.Find(dest);
        Check.NotNull(live, "the entry the library holds now points at the archive");
        Check.True(live!.IsArchived, "and is stamped archived");
        Check.Equal(dest, lib.Find(trim)!.SourcePath, "the trim cut from it follows");
        var fresh = new ClipLibrary();
        fresh.Load(clips.Path);
        Check.True(fresh.Find(dest)?.IsArchived == true, "the index on disk has it archived (a reload does not drop it)");

        // A clip that left the library meanwhile: nothing is deleted.
        var trimEntry = lib.Find(trim)!;
        lib.Remove(trimEntry);
        var gone = lib.MoveToArchive(trimEntry, trim, archive.File("a-trim.mp4"), () => { deletes++; return true; });
        Check.Equal(ArchiveMove.NotInLibrary, gone); Check.Equal(1, deletes, "the delete did not run");
        Check.True(File.Exists(trim), "the file is still there");

        // A delete that does not happen: nothing changes.
        var kept = lib.Add(trim, 1, 50, null, Array.Empty<string>());
        var refused = lib.MoveToArchive(kept, trim, archive.File("a-trim.mp4"), () => false);
        Check.Equal(ArchiveMove.NotDeleted, refused);
        Check.Equal(trim, lib.Find(trim)!.Path, "still local"); Check.False(lib.Find(trim)!.IsArchived, "not archived");
    }

    [UnitTest]
    public static void A_reload_cannot_run_between_the_delete_and_the_save()
    {
        using var meta = new TempDir();
        using var clips = new TempDir();
        using var archive = new TempDir();
        ClipLibrary.IndexPathOverride = meta.File("clips.json");
        string src = clips.File("a.mp4"), dest = archive.File("a.mp4");
        File.WriteAllBytes(src, new byte[100]);
        File.WriteAllBytes(dest, new byte[100]);
        var lib = new ClipLibrary();
        lib.Load(clips.Path);
        var entry = lib.Find(src)!;

        var reload = new Thread(() => lib.Load(clips.Path));
        var moved = lib.MoveToArchive(entry, src, dest, () =>
        {
            // The reload starts now and has to wait for the lock; without the lock it would run right after the delete below,
            // find the local file gone, drop the entry, and the save would then write a library without the clip.
            reload.Start();
            Thread.Sleep(150);
            File.Delete(src);
            return true;
        });
        reload.Join();
        Check.Equal(ArchiveMove.Moved, moved);
        var all = lib.Entries;
        Check.Equal(1, all.Count(e => e.IsArchived && e.Path == dest), "the archived clip is in the library exactly once");
        Check.Equal(0, all.Count(e => e.Path == src), "and nothing is left of the local path");
    }
}
