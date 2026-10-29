using System.IO;
using Clink.Audio;
using Clink.Encode;
using Clink.Engine;
using Clink.Screenshots;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>The documentation-screenshot aids (tools/screenshots): <c>--dev-no-marker</c>, <c>--dev-demo-status</c> and the sample-clips plan / index / command line.</summary>
internal static class DocScreenshotTests
{
    private static DevOptions Quiet(TempDir dir, params string[] more) => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-quiet" }.Concat(more).ToArray())!;

    // ------------------------------------------------------------------ the switches

    [UnitTest]
    public static void No_marker_and_demo_status_need_dev_and_dev_quiet()
    {
        using var dir = new TempDir();
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev-no-marker" }), "no --dev at all");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev-demo-status", "NeonDrift" }), "no --dev at all");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev-quiet", "--dev-no-marker" }), "--dev-quiet alone");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-no-marker" }), "--dev without --dev-quiet");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-demo-status", "NeonDrift" }), "--dev without --dev-quiet");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path, "--dev-no-marker", "--dev-demo-status", "X" }), "both, still no --dev-quiet");

        var plain = Quiet(dir);
        Check.False(plain.NoMarker, "off unless asked");
        Check.True(plain.DemoStatus == null, "no demo status unless asked");
        var marker = Quiet(dir, "--dev-no-marker");
        Check.True(marker.NoMarker && marker.Quiet);
        Check.True(marker.DemoStatus == null);
        var demo = Quiet(dir, "--DEV-DEMO-STATUS", "NeonDrift", "--Dev-No-Marker");
        Check.Equal("NeonDrift", demo.DemoStatus);
        Check.True(demo.NoMarker, "case does not matter, order does not matter");
        Check.Equal("Neon Drift", Quiet(dir, "--dev-demo-status", "\"Neon Drift\"").DemoStatus, "a quoted name loses its quotes");
        Check.True(DevOptions.Parse(new[] { "--dev-no-marker", "--dev-quiet", "--dev", dir.Path })!.NoMarker, "before --dev too");
    }

    [UnitTest]
    public static void Demo_status_needs_a_game_and_look_alikes_are_errors()
    {
        using var dir = new TempDir();
        Check.Throws<ArgumentException>(() => Quiet(dir, "--dev-demo-status"), "no value");
        Check.Throws<ArgumentException>(() => Quiet(dir, "--dev-demo-status", "--dev-no-marker"), "the next switch is not the value");
        Check.Throws<ArgumentException>(() => Quiet(dir, "--dev-demo-status", "   "), "blank");
        Check.Throws<ArgumentException>(() => Quiet(dir, "--dev-no-markers"), "a look-alike");
        Check.Throws<ArgumentException>(() => Quiet(dir, "-dev-no-marker"), "a look-alike with one dash");
        Check.Throws<ArgumentException>(() => Quiet(dir, "--dev-demo-statu", "X"), "a look-alike");
    }

    [UnitTest]
    public static void A_restart_keeps_no_marker_and_demo_status_and_the_helper_drops_them()
    {
        var list = DevOptions.Switches(@"C:\d", false, false, false, null, quiet: true, noMarker: true, demoStatus: "NeonDrift");
        Check.Equal(new[] { "--dev", @"C:\d", "--dev-quiet", "--dev-no-marker", "--dev-demo-status", "NeonDrift" }, list.ToArray());
        Check.Equal(new[] { "--dev", @"C:\d", "--dev-quiet" }, DevOptions.Switches(@"C:\d", false, false, false, null, quiet: true).ToArray(), "nothing extra unless asked");
        Check.Equal(new[] { "--show" }, DevOptions.WithoutSwitches(new[] { "--dev", @"C:\d", "--dev-quiet", "--dev-demo-status", "NeonDrift", "--dev-no-marker", "--show" }).ToArray());
    }

    // ------------------------------------------------------------------ the gating

    [UnitTest]
    public static void The_marker_is_hidden_only_in_a_quiet_dev_instance_that_asked_for_it()
    {
        Check.False(QuietMode.HidesMarker(false, false, false), "normal run");
        Check.False(QuietMode.HidesMarker(false, true, true), "flags without --dev");
        Check.False(QuietMode.HidesMarker(true, false, true), "plain --dev with the flag but not quiet");
        Check.False(QuietMode.HidesMarker(true, true, false), "quiet without the flag keeps the marker");
        Check.True(QuietMode.HidesMarker(true, true, true), "--dev --dev-quiet --dev-no-marker");

        Check.False(AppController.ShowsDevMarker, "a test process is not a dev instance: no marker to show");
        AppController.InstanceSuffix = ".dev.test";
        Check.True(AppController.ShowsDevMarker, "plain --dev shows it");
        AppController.DevNoMarker = true;
        Check.True(AppController.ShowsDevMarker, "the flag alone, without --dev-quiet, hides nothing");
        AppController.DevQuiet = true;
        Check.False(AppController.ShowsDevMarker, "--dev --dev-quiet --dev-no-marker hides it");
        AppController.DevNoMarker = false;
        Check.True(AppController.ShowsDevMarker, "quiet alone keeps it");
        // the state is put back by the runner after the test
    }

    [UnitTest]
    public static void The_demo_status_is_a_display_text_only_in_a_quiet_dev_instance()
    {
        Check.True(QuietMode.DemoStatusText(false, false, "NeonDrift") == null, "normal run");
        Check.True(QuietMode.DemoStatusText(false, true, "NeonDrift") == null, "no --dev");
        Check.True(QuietMode.DemoStatusText(true, false, "NeonDrift") == null, "plain --dev");
        Check.True(QuietMode.DemoStatusText(true, true, null) == null, "no game");
        Check.True(QuietMode.DemoStatusText(true, true, "  ") == null, "blank game");
        Check.Equal("Recording · NeonDrift", QuietMode.DemoStatusText(true, true, " NeonDrift "));

        Check.True(AppController.DemoStatusText == null, "a test process shows its real status");
        AppController.InstanceSuffix = ".dev.test";
        AppController.DevDemoStatus = "NeonDrift";
        Check.True(AppController.DemoStatusText == null, "not quiet");
        AppController.DevQuiet = true;
        Check.Equal("Recording · NeonDrift", AppController.DemoStatusText);
        Check.True(AppController.StartsPaused, "the demo status records nothing: the instance still starts paused");
    }

    // ------------------------------------------------------------------ the sample clips

    [UnitTest]
    public static void The_sample_plan_has_about_eight_clips_with_nested_trims_and_shared_ones()
    {
        var now = new DateTime(2026, 10, 27, 15, 30, 0);
        var plan = SampleClips.Plan(now);
        Check.True(plan.Count is >= 8 and <= 10, "about eight clips");
        Check.True(plan.All(s => s.Created <= now && s.Created > now.AddDays(-8)), "all within the last week, none in the future");
        Check.True(plan.All(s => s.Seconds is >= 1.5 and <= 4.0), "2 to 4 s (trims may be a little shorter)");
        Check.True(plan.Where(s => !s.IsTrim).All(s => s.Seconds >= 2.5), "recordings are at least 2.5 s");
        int trims = plan.Count(s => s.IsTrim);
        Check.True(trims is >= 2 and <= 3, "two or three trims");
        Check.True(plan.Count(s => s.Shared) is >= 2 and <= 3, "a couple shared");
        Check.True(plan.Where(s => !s.IsTrim).Select(s => s.Game).Distinct().Count() >= 6, "several different games");
        Check.True(plan.Select(s => s.Scene).Distinct().Count() >= 6, "several different looks");
        for (int i = 0; i < plan.Count; i++)
            if (plan[i].IsTrim)
            {
                Check.True(plan[i].TrimOf is >= 0 && plan[i].TrimOf < i, "a trim comes after its source in the plan");
                Check.False(plan[plan[i].TrimOf].IsTrim, "a trim is cut from a recording");
                Check.Equal(plan[plan[i].TrimOf].Game, plan[i].Game, "same game as its source");
                Check.True(plan[i].Created > plan[plan[i].TrimOf].Created, "a trim is newer than its source");
                Check.True(plan[i].StartAt + plan[i].Seconds <= plan[plan[i].TrimOf].Seconds + 1e-9, "a trim is a part of its source");
            }
        Check.Equal(plan.Select(s => s.Created).Distinct().Count(), plan.Count, "no two clips share a time");
        Check.Equal(plan.Select(x => x.Created).ToArray(), SampleClips.Plan(now).Select(x => x.Created).ToArray(), "the plan is deterministic");
    }

    [UnitTest]
    public static void Sample_file_names_follow_the_engines_and_the_trim_editors()
    {
        var plan = SampleClips.Plan(new DateTime(2026, 10, 27, 15, 30, 0));
        var names = SampleClips.FileNames(plan);
        Check.Equal(plan.Count, names.Count);
        Check.Equal(names.Count, names.Select(n => n.ToLowerInvariant()).Distinct().Count(), "unique names");
        Check.True(names.All(n => n.EndsWith(".mp4") && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0), "valid .mp4 names");
        Check.True(Regex_matches(names[0], @"^NeonDrift \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}\.mp4$"), "recordings: game and stamp");
        string stem = Path.GetFileNameWithoutExtension(names[0]);
        Check.Equal(stem + " (trim).mp4", names[7], "the first trim");
        Check.True(names.All(n => !n.Contains("(trim 2)")), "no \"(trim 2)\"");
        var trimGames = plan.Where(s => s.IsTrim).Select(s => s.Game).ToList();
        Check.Equal(trimGames.Count, trimGames.Distinct().Count(), "the trims are of different games");
    }

    [UnitTest]
    public static void Trim_titles_are_distinct_in_the_sample_plan()
    {
        var plan = SampleClips.Plan(new DateTime(2026, 10, 27, 15, 30, 0));
        var names = SampleClips.FileNames(plan);
        var titles = names.Where((_, i) => plan[i].IsTrim).Select(n => Path.GetFileNameWithoutExtension(n)).ToList();
        Check.Equal(2, titles.Count, "two trims");
        Check.Equal(titles.Count, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "no two trims carry the same title");
    }

    [UnitTest]
    public static void ShowsDevMarker_follows_DocShots()
    {
        Check.False(AppController.DocShots, "a test process is not a dev instance");
        AppController.InstanceSuffix = ".dev.test";
        AppController.DevQuiet = true;
        Check.False(AppController.DocShots, "dev + quiet is not enough");
        Check.True(AppController.ShowsDevMarker);
        AppController.DevNoMarker = true;
        Check.True(AppController.DocShots, "dev + quiet + no-marker");
        Check.False(AppController.ShowsDevMarker);
        Check.True(typeof(AppController).GetProperty("TrayName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!.ToString()!.Contains("TEST"), "the tray name keeps TEST (it never appears in a PrintWindow picture)");
        AppController.DevQuiet = false;
        Check.False(AppController.DocShots, "no-marker without quiet");
        Check.True(AppController.ShowsDevMarker);
        AppController.InstanceSuffix = "";
        AppController.DevQuiet = true;
        Check.False(AppController.DocShots, "not a dev instance");
        // the state is put back by the runner after the test
    }

    [UnitTest]
    public static void A_sample_clip_encodes_and_decodes()
    {
        const int w = 320, h = 180, frames = 15;
        const uint fps = 30;
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "sample.mp4");
        string? skip = null;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var canvas = new SampleCanvas(w, h);
                var nv12 = new byte[w * h * 3 / 2];
                const long origin = 1_000_000_000L;
                SoftwareH264Encoder enc;
                try { enc = new SoftwareH264Encoder(w, h, fps, 1_000_000); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("No software H.264 encoder", StringComparison.Ordinal)) { skip = ex.Message; return; }
                using (enc)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        SampleFrames.Draw(canvas, SampleScene.Plasma, f / (double)fps, 7);
                        canvas.ToNv12(nv12);
                        enc.Encode(nv12, origin + (long)Math.Round(f * 10_000_000.0 / fps));
                    }
                    var video = enc.Finish();
                    Check.True(video.Count > 0 && video[0].Keyframe, "the first sample is a keyframe");
                    ClipWriter.Write(path, video, "h264", w, h, enc.SequenceHeader, new List<(AudioTrackDesc desc, List<AacFrame> frames)>(), origin, fps);
                }
                var frame = MediaProbe.DecodeFrame(path, 0);
                Check.True(frame != null, "the first frame decodes");
                Check.Equal(w, frame!.Value.w, "decoded width");
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (failure != null) throw new TestFailure(failure is TestFailure ? failure.Message : failure.ToString());
        if (skip != null) Check.Skip(skip);
    }

    private static bool Regex_matches(string s, string pattern) => System.Text.RegularExpressions.Regex.IsMatch(s, pattern);

    [UnitTest]
    public static void The_sample_index_nests_the_trims_under_their_sources()
    {
        var plan = SampleClips.Plan(new DateTime(2026, 10, 27, 15, 30, 0));
        string folder = @"C:\dev\clips";
        var results = plan.Select((s, i) => (s.Seconds, 1_000_000L + i)).ToList();
        var index = SampleClips.BuildIndex(plan, folder, results);
        Check.Equal(plan.Count, index.Count);
        Check.True(index.Zip(index.Skip(1), (a, b) => a.Created >= b.Created).All(ok => ok), "newest first");
        Check.True(index.All(e => e.Path.StartsWith(folder + "\\", StringComparison.Ordinal)), "all inside the clips folder");
        Check.Equal(plan.Count, index.Select(e => e.Path.ToLowerInvariant()).Distinct().Count(), "unique paths");
        Check.Equal(plan.Count(s => s.IsTrim), index.Count(e => e.IsTrim), "trims marked");
        Check.True(index.Where(e => e.IsTrim).All(e => e.SourcePath != null && index.Any(o => !o.IsTrim && o.Path == e.SourcePath)), "a trim's SourcePath is a recording in the index");
        Check.True(index.Where(e => !e.IsTrim).All(e => e.SourcePath == null));
        Check.Equal(plan.Count(s => s.Shared), index.Count(e => e.IsShared), "shared ones carry a link");
        Check.True(index.All(e => e.Duration > 0 && e.Size > 0 && !string.IsNullOrEmpty(e.Game)));
        // the library's own grouping puts the trims under their sources
        var rows = ClipGrouping.Flatten(index, e => e, ClipGrouping.NewExpanded());
        Check.Equal(plan.Count(s => !s.IsTrim), rows.Count(r => r.Kind != ClipRowKind.Child) , "collapsed: only recordings at the top");
        Check.Equal(2, rows.Count(r => r.Kind == ClipRowKind.Parent), "two recordings have trims");
        Check.Throws<ArgumentException>(() => SampleClips.BuildIndex(plan, folder, results.Take(3).ToList()), "one result per clip");
    }

    [UnitTest]
    public static void The_samples_command_line_and_its_refusals()
    {
        var (folder, index) = SampleClips.ParseArgs(new[] { "--dev-make-samples", @"C:\work\shots\dev\clips" });
        Check.Equal(@"C:\work\shots\dev\clips", folder);
        Check.Equal(@"C:\work\shots\dev\clips.json", index, "the index goes beside the clips folder");
        var (_, own) = SampleClips.ParseArgs(new[] { "--dev-make-samples", @"C:\work\clips", "--index", @"C:\work\idx\my.json" });
        Check.Equal(@"C:\work\idx\my.json", own);
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples" }), "needs a folder");
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", "--index", "x" }), "a switch is not a folder");
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", @"C:\a", "--oops" }), "unknown argument");
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", @"C:\a", "--index" }), "--index needs a file");

        var real = new List<(string Path, string What)> { (@"C:\Users\x\AppData\Roaming\Clink", "the real settings"), (@"C:\Users\x\Videos\Clink", "the default clips folder") };
        Check.True(SampleClips.Refusal(@"C:\work\dev\clips", @"C:\work\dev\clips.json", real) == null, "a folder of its own is fine");
        Check.True(SampleClips.Refusal(@"C:\Users\x\Videos\Clink", @"C:\work\clips.json", real) != null, "the real clips folder");
        Check.True(SampleClips.Refusal(@"C:\Users\x\Videos\Clink\sub", @"C:\work\clips.json", real) != null, "inside it");
        Check.True(SampleClips.Refusal(@"C:\Users\x\Videos", @"C:\work\clips.json", real) != null, "a parent of it");
        Check.True(SampleClips.Refusal(@"C:\work\clips", @"C:\Users\x\AppData\Roaming\Clink\clips.json", real) != null, "an index in the real settings folder");
    }

    // ------------------------------------------------------------------ refusals and display-only paths

    [UnitTest]
    public static void No_marker_cannot_be_combined_with_quiet_record()
    {
        using var dir = new TempDir();
        var ex = Check.Throws<ArgumentException>(() => Quiet(dir, "--dev-no-marker", "--dev-quiet-record"));
        Check.True(ex.Message.Contains("--dev-no-marker") && ex.Message.Contains("--dev-quiet-record"), "the message names both");
        Check.Throws<ArgumentException>(() => Quiet(dir, "--dev-quiet-record", "--dev-no-marker"), "in either order");
        Check.True(Quiet(dir, "--dev-quiet-record").QuietRecord, "quiet-record alone is fine");
    }

    [UnitTest]
    public static void Doc_shot_paths_are_display_only_and_settings_keep_the_real_ones()
    {
        using var dir = new TempDir();
        var s = new Settings { ClipsFolder = dir.File("clips") };
        string before = s.ClipsFolder;
        string shownClips = QuietMode.Shown(true, s.ClipsFolder, QuietMode.DocClipsFolder);
        string shownDir = QuietMode.Shown(true, dir.Path, QuietMode.DocSettingsDir);
        string shownShots = QuietMode.Shown(true, ScreenshotService.FolderFor(s), QuietMode.DocScreenshotsFolder);
        Check.Equal(@"%USERPROFILE%\Videos\Clink", shownClips);
        Check.Equal(@"%APPDATA%\Clink", shownDir);
        Check.Equal(@"%USERPROFILE%\Videos\Clink\Screenshots", shownShots);
        Check.True(shownClips != s.ClipsFolder && shownDir != dir.Path && shownShots != ScreenshotService.FolderFor(s), "the placeholders differ from the real paths");
        Check.False(shownClips.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) || shownDir.Contains("Temp", StringComparison.OrdinalIgnoreCase), "no user name, no scratch path");
        Check.Equal(before, s.ClipsFolder, "showing the placeholder leaves the stored clips folder alone");
        Check.False(Path.IsPathFullyQualified(shownClips) || Path.IsPathFullyQualified(shownDir) || Path.IsPathFullyQualified(shownShots),
            "a placeholder is not a full path, so the path-box handler would refuse it (it also returns at once in doc-shot mode)");
        Check.Equal(s.ClipsFolder, QuietMode.Shown(false, s.ClipsFolder, QuietMode.DocClipsFolder), "outside doc-shot mode the real path is shown");
        Check.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink"), new Settings().ClipsFolder, "the placeholder names the default clips folder");
    }

    [UnitTest]
    public static void The_demo_game_reads_as_detected_only_in_a_quiet_dev_instance()
    {
        Check.True(QuietMode.DemoGameName(true, false, "NeonDrift") == null, "not quiet");
        Check.True(QuietMode.DemoGameName(false, true, "NeonDrift") == null, "not dev");
        Check.True(QuietMode.DemoGameName(true, true, " ") == null, "blank");
        Check.Equal("NeonDrift", QuietMode.DemoGameName(true, true, " NeonDrift "));
        Check.True(AppController.DemoGame == null, "a test process has none");
    }

    [UnitTest]
    public static void Sample_toasts_name_the_demo_game_and_otherwise_keep_the_built_in_one()
    {
        Check.Equal("Overwatch", QuietMode.SampleToastGame(null), "no demo game");
        Check.Equal("Overwatch", QuietMode.SampleToastGame("  "), "blank");
        Check.Equal("NeonDrift", QuietMode.SampleToastGame(" NeonDrift "), "the demo game");
        Check.Equal("Overwatch", QuietMode.SampleToastGame(AppController.DemoGame), "a test process has no demo game");
    }

    private static string? Written(TempDir dir, string folder, string index)
    {
        var plan = SampleClips.Plan(new DateTime(2026, 10, 27, 15, 30, 0));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SampleClips.MarkerName), "marker");
        File.WriteAllText(index, System.Text.Json.JsonSerializer.Serialize(SampleClips.BuildIndex(plan, folder, plan.Select(p => (p.Seconds, 1L)).ToList())));
        return SampleClips.TargetProblem(folder, index);
    }

    [UnitTest]
    public static void Samples_target_must_be_empty_or_one_an_earlier_run_made()
    {
        using var dir = new TempDir();
        string index = dir.File("clips.json");
        Check.True(SampleClips.TargetProblem(dir.File("new"), index) == null, "a folder that does not exist yet");
        string empty = dir.File("empty"); Directory.CreateDirectory(empty);
        Check.True(SampleClips.TargetProblem(empty, index) == null, "an empty folder");
        string full = dir.File("full"); Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "mine.txt"), "x");
        Check.True(SampleClips.TargetProblem(full, index) != null, "a non-empty folder without the marker is refused");
        Check.True(SampleClips.TargetProblem(Path.Combine(full, "mine.txt"), index) != null, "a file is refused");

        string again = dir.File("again"), againIndex = dir.File("again.json");
        Check.True(Written(dir, again, againIndex) == null, "a marked folder with its own index is written into again");
        File.WriteAllText(Path.Combine(again, "leftover.mp4"), "x");
        Check.True(SampleClips.TargetProblem(again, againIndex) == null, "and may hold other files of the earlier run");

        File.WriteAllText(againIndex, """[ { "Path": "C:\\somewhere\\else\\a.mp4" } ]""");
        Check.True(SampleClips.TargetProblem(again, againIndex) != null, "an index that lists other places is not ours");
        File.WriteAllText(againIndex, "[]");
        Check.True(SampleClips.TargetProblem(again, againIndex) != null, "an empty index is not one a samples run wrote");
        File.WriteAllText(againIndex, "not json");
        Check.True(SampleClips.TargetProblem(again, againIndex) != null, "an index that is not an index is not ours");
        File.WriteAllText(index, "[]");
        Check.True(SampleClips.TargetProblem(dir.File("new2"), index) != null, "an existing index beside an unmarked folder is not overwritten");
        Directory.CreateDirectory(dir.File("idxdir"));
        Check.True(SampleClips.TargetProblem(dir.File("new3"), dir.File("idxdir")) != null, "an index that is a folder");
    }

    [UnitTest]
    public static void Samples_refuse_a_junction_to_a_real_place_a_drive_root_and_the_profile()
    {
        using var dir = new TempDir();
        string real = dir.File(@"real\clink"); Directory.CreateDirectory(real);
        var places = new List<(string Path, string What)> { (real, "the real clips") };
        string index = dir.File("clips.json");
        Check.True(SampleClips.Refusal(dir.File("own"), index, places) == null, "a folder of its own");
        Check.True(SampleClips.Refusal(real, index, places) != null, "the real place itself");

        string root = Path.GetPathRoot(dir.Path)!;
        Check.True(SampleClips.Refusal(root, index, places) != null, "a drive root");
        Check.True(SampleClips.Refusal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), index, places) != null, "the user profile folder");

        string link = dir.File("link");
        if (!TestJunction.TryMake(link, real, out string why)) { TestJunction.Skipped(nameof(Samples_refuse_a_junction_to_a_real_place_a_drive_root_and_the_profile), why); return; }
        try
        {
            string r = SampleClips.Refusal(link, index, places) ?? "";
            Check.True(r.Length > 0 && r.Contains("really"), "a junction that leads to a real place is refused, naming where it leads: " + r);
            Check.True(SampleClips.Refusal(Path.Combine(link, "sub"), index, places) != null, "and so is a folder to be made inside it");
            Check.True(SampleClips.Refusal(dir.File("own"), Path.Combine(link, "clips.json"), places) != null, "an index written through it");
        }
        finally { TestJunction.Remove(link); }
    }

    [UnitTest]
    public static void Samples_command_only_runs_with_the_dev_flag()
    {
        using var dir = new TempDir();
        string folder = dir.File("clips");
        int attached = 0;
        Check.True(SampleClips.TryRunCli(new[] { "--dev-make-samples", folder }, () => attached++, out int code), "it is the samples command");
        Check.Equal(2, code, "refused without --dev");
        Check.Equal(1, attached);
        Check.False(Directory.Exists(folder), "nothing was written");
        Check.False(SampleClips.HasDevFlag(new[] { "--dev-make-samples", "--dev" }), "the folder position is not a flag");
        Check.False(SampleClips.HasDevFlag(new[] { "--dev-make-samples", folder, "--index", "--dev" }), "an index value is not a flag");
        Check.False(SampleClips.HasDevFlag(new[] { "--dev-make-samples", folder, "-dev" }), "a look-alike is not the flag");
        Check.True(SampleClips.HasDevFlag(new[] { "--dev-make-samples", folder, "--dev" }));
        Check.True(SampleClips.HasDevFlag(new[] { "--dev-make-samples", folder, "--index", dir.File("i.json"), "--DEV" }));
        var (f, i) = SampleClips.ParseArgs(new[] { "--dev-make-samples", folder, "--dev" });
        Check.Equal(Path.GetFullPath(folder), f, "--dev is accepted by the parser");
        Check.Equal(Path.Combine(dir.Path, "clips.json"), i);
    }

    [UnitTest]
    public static void Samples_index_must_be_a_json_file_without_stream_colons_and_is_written_without_overwriting()
    {
        using var dir = new TempDir();
        var none = new List<(string Path, string What)>();
        string own = dir.File("own");
        // --index must name a .json file (so it can never be a drive root or the profile folder), in any case
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", own, "--index", Path.GetPathRoot(dir.Path)! }), "a drive root");
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", own, "--index", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }), "the profile folder");
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", own, "--index", dir.File("notes.txt") }), "not .json");
        Check.Equal(dir.File("UP.JSON"), SampleClips.ParseArgs(new[] { "--dev-make-samples", own, "--index", dir.File("UP.JSON") }).Index, ".JSON counts");
        // a ':' after the drive is an alternate data stream, in the folder and in the index
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", dir.Path + @"\clips:stream" }), "a stream in the folder");
        Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", own, "--index", dir.Path + @"\a:b.json" }), "a colon in the index");
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", dir.Path + @"\clips:stream", "--dev" }, _ => { }), "Run refuses it too");
        Check.True(SampleClips.Refusal(own, dir.File("ok.json"), none) == null);

        string idx = dir.File("new.json");
        SampleClips.WriteIndex(idx, own, "[]");
        Check.Equal("[]", File.ReadAllText(idx));
        // an existing index that is not ours is never written
        Check.Throws<IOException>(() => SampleClips.WriteIndex(idx, own, "[1]"));
        Check.Equal("[]", File.ReadAllText(idx));
        // our own is replaced
        File.WriteAllText(idx, $"[ {{ \"Path\": {System.Text.Json.JsonSerializer.Serialize(Path.Combine(own, "a.mp4"))} }} ]");
        SampleClips.WriteIndex(idx, own, "[2]");
        Check.Equal("[2]", File.ReadAllText(idx));
    }

    [UnitTest]
    public static void A_path_that_cannot_be_resolved_is_refused_not_waved_through()
    {
        using var dir = new TempDir();
        var none = new List<(string Path, string What)>();
        // what does not exist yet, under a folder that does, is still fine
        Check.True(SampleClips.Refusal(dir.File(@"not\yet\there"), dir.File("i.json"), none) == null);
        Check.NotNull(AppDataRedirect.ResolvedPath(dir.Path), "an existing folder resolves");

        // a junction whose target is gone leads nowhere: ResolvedPath cannot say, so everything through it is refused
        string target = dir.File("target"), link = dir.File("link");
        Directory.CreateDirectory(target);
        if (!TestJunction.TryMake(link, target, out string why)) { TestJunction.Skipped(nameof(A_path_that_cannot_be_resolved_is_refused_not_waved_through), why); return; }
        try
        {
            Directory.Delete(target);
            string below = Path.Combine(link, "new");
            Check.Null(AppDataRedirect.ResolvedPath(link), "a dangling junction does not resolve");
            Check.Null(AppDataRedirect.ResolvedPath(below), "nor does what is below it");
            Check.Throws<IOException>(() => SampleClips.Refusal(below, dir.File("i.json"), none), "Refusal: the folder");
            Check.Throws<IOException>(() => SampleClips.Refusal(dir.File("own"), Path.Combine(link, "i.json"), none), "Refusal: the index");
            Check.Contains(Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", below })).Message, "cannot be checked", "DevOptions.Parse");
            Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", link, "--dev" }, _ => { }), "Run: the link");
            Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", below, "--dev" }, _ => { }), "Run: below the link");
            Check.False(Directory.Exists(below), "nothing was made");
        }
        finally { TestJunction.Remove(link); }
    }

    [UnitTest]
    public static void A_path_in_the_device_form_is_refused()
    {
        using var dir = new TempDir();
        var none = new List<(string Path, string What)>();
        string place = dir.File("place");
        foreach (string prefix in new[] { @"\??\", @"\\?\", @"\\.\", "//?/" })
        {
            string folder = prefix + place + @"\new";
            Check.True(SampleClips.Refusal(folder, dir.File("i.json"), none) != null, "Refusal: " + prefix);
            Check.True(SampleClips.Refusal(dir.File("own"), prefix + dir.File("i.json"), none) != null, "Refusal, index: " + prefix);
            Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", folder, "--dev" }), "ParseArgs folder: " + prefix);
            Check.Throws<ArgumentException>(() => SampleClips.ParseArgs(new[] { "--dev-make-samples", dir.File("own"), "--index", prefix + dir.File("i.json") }), "ParseArgs index: " + prefix);
            Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", folder, "--dev" }, _ => { }), "Run: " + prefix);
            Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", folder }), "DevOptions.Parse: " + prefix);
        }
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", @"\\?\GLOBALROOT\Device\HarddiskVolume1\x" }), "GLOBALROOT");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", @"\\?\Volume{00000000-0000-0000-0000-000000000000}\x" }), "Volume{GUID}");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", dir.Path + @"\dev:stream" }), "a stream colon");
        // the fake package layout of the import tests, in the \??\ form: refused, not read as a folder that is "somewhere else"
        string fakePackage = dir.File(@"AppData\Local\Packages\Fake_abc\LocalCache\Roaming\Clink");
        Check.Throws<ArgumentException>(() => DevOptions.Parse(new[] { "--dev", @"\??\" + fakePackage }), @"\??\ fake package folder");
    }

    [UnitTest]
    public static void Samples_run_exit_codes()
    {
        using var dir = new TempDir();
        var said = new List<string>();
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples" }, said.Add), "no folder");
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", dir.File("c"), "--oops" }, said.Add), "unknown argument");
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Clink") }, said.Add), "the default clips folder");
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", Path.GetPathRoot(dir.Path)! }, said.Add), "a drive root");
        string full = dir.File("full"); Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "x.txt"), "x");
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", full }, said.Add), "a non-empty folder that is not ours");
        Check.True(!File.Exists(Path.Combine(full, SampleClips.MarkerName)), "nothing was written into it");
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", new string('a', 40000) }, said.Add), "a path that is far too long");
        Check.Equal(2, SampleClips.Run(new[] { "--dev-make-samples", dir.File("ok"), "--index", new string('b', 40000) }, said.Add), "an index path that is far too long");
        Check.True(said.Count == 7 && said.All(l => l.StartsWith("Clink: ")), "each refusal said why");
        Check.False(SampleClips.TryRunCli(new[] { "--dev" }, () => { }, out int code) || code != 0, "another command line is not ours");
        Check.False(SampleClips.TryRunCli(Array.Empty<string>(), () => { }, out _), "no arguments");
    }
}