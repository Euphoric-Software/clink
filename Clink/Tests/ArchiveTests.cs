using System.IO;
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

        /// <summary>A virus scanner may still be reading a file that was just written, and the service skips a file that is open: let it finish first.</summary>
        private static void WaitUntilNobodyHasItOpen(string path)
        {
            for (int i = 0; i < 100; i++)
            {
                try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return; }
                catch (IOException) { Thread.Sleep(20); }
            }
        }

        public void Load() => Library.Load(Clips.Path);

        public ArchiveRunResult Run(ArchiveScope scope, bool throttle = false, IProgress<ArchiveProgress>? progress = null, CancellationToken ct = default)
        {
            var task = Service.RunManualAsync(scope, throttle, progress, ct);
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
}
