using Clink.Services;

namespace Clink.Tests;

/// <summary>The share-fit model (<see cref="ShareFitModel"/>): the target a Discord plan maps to, the states and every text the windows show, exactly.</summary>
internal static class ShareFitModelTests
{
    private const string Dot = " \u00B7 ";   // the middle dot between the details

    private static ShareFitTarget Free => ShareFitModel.ForPlan("free");

    /// <summary>The plan the design document uses: 20 MB, 45 s, a 46 MB 1080p60 clip with one audio track = 720p60 at 3.1 Mbps.</summary>
    private static ShareFitPlan DesignPlan()
    {
        var r = ShareFit.Plan(1920, 1080, 60, 45, 46_000_000, 20_000_000, "h264", Capabilities.AudioKbps, 1, "h264");
        Check.Equal(ShareFitKind.Plan, r.Kind);
        Check.Equal("720p60", r.Plan!.Label);
        return r.Plan!;
    }

    [UnitTest]
    public static void A_Discord_plan_maps_to_the_size_targets_and_nothing_else()
    {
        foreach (var (plan, id) in new[] { ("free", "discord10"), ("basic", "discord50"), ("nitro", "discord500") })
        {
            var t = ShareFitModel.ForPlan(plan);
            var info = Capabilities.SizeTargetFor(id);
            Check.Equal(info.Bytes, t.LimitBytes, plan + ": the bytes come from Capabilities");
            Check.Equal(info.Name, t.Label, plan + ": and so does the label");
            Check.Equal(0, t.MaxSeconds, plan + ": Discord has no length limit");
            Check.Equal("Discord", t.Service);
            Check.Equal(id, ShareFitModel.TargetIdFor(plan));
        }
        Check.Equal(20_000_000L, ShareFitModel.ForPlan("free").LimitBytes);
        Check.Equal("Discord free (20 MB)", ShareFitModel.ForPlan("free").Label);
        Check.Equal("Discord Nitro Basic (50 MB)", ShareFitModel.ForPlan("basic").Label);
        Check.Equal("Discord Nitro (1 GB)", ShareFitModel.ForPlan("nitro").Label);
        Check.Equal(1_000_000_000L, ShareFitModel.ForPlan("nitro").LimitBytes);
        foreach (string odd in new[] { null!, "", "gold", "NITRO ", "discord50" })
            Check.Equal(odd?.Trim().ToLowerInvariant() == "nitro" ? 1_000_000_000L : 20_000_000L, ShareFitModel.ForPlan(odd).LimitBytes, $"'{odd}'");

        var s = Settings.FromJson("""{ "SizeTarget": "discord50" }""");
        Check.Equal(50_000_000L, ShareFitModel.ForSettings(s).LimitBytes, "the setting, normalized from the size target");
        s.DiscordPlan = "nitro";
        Check.Equal("Discord Nitro (1 GB)", ShareFitModel.ForSettings(s).Label);

        var copium = ShareFitModel.ForCopium(false);
        Check.Equal((Capabilities.CopiumFreeBytes, Capabilities.CopiumFreeSeconds, "Copium"), (copium.LimitBytes, copium.MaxSeconds, copium.Service));
        var plus = ShareFitModel.ForCopium(true);
        Check.Equal((Capabilities.CopiumPlusBytes, Capabilities.CopiumPlusSeconds), (plus.LimitBytes, plus.MaxSeconds));
    }

    [UnitTest]
    public static void The_share_window_texts_are_exactly_the_designed_ones()
    {
        Check.Equal("Too big for Discord free (20 MB): this clip is 46 MB", ShareFitModel.OverText(Free, 46_000_000));
        Check.Equal("Make a 20 MB copy", ShareFitModel.MakeCopyText(Free));
        Check.Equal("Make a 50 MB copy", ShareFitModel.MakeCopyText(ShareFitModel.ForPlan("basic")));
        Check.Equal("Make a 1 GB copy", ShareFitModel.MakeCopyText(ShareFitModel.ForPlan("nitro")));
        Check.Equal("Making a 20 MB copy for Discord: 720p60 at 3.1 Mbps... 42%", ShareFitModel.WorkingText(Free, DesignPlan(), 0.42));
        Check.Equal("Making a 20 MB copy for Discord... 3%", ShareFitModel.WorkingText(Free, null, 0.03), "before the clip has been planned");
        Check.Equal("Making a 20 MB copy for Discord... 100%", ShareFitModel.WorkingText(Free, null, 7), "clamped");
        Check.Equal("Waiting...", ShareFitModel.WaitingText);
        Check.Equal("Use the original", ShareFitModel.UseOriginalText);
        Check.Equal("19.1 MB" + Dot + "0:45" + Dot + "720p60", ShareFitModel.DoneDetails(19_100_000, 45, "720p60"));
        Check.Equal("19.1 MB" + Dot + "0:45", ShareFitModel.DoneDetails(19_100_000, 45, ""), "without a label");
        Check.Equal("820 kB" + Dot + "1:05", ShareFitModel.DoneDetails(820_000, 65, null), "below 1 MB it is kB");
        Check.Equal("Trim it to about 0:55", ShareFitModel.TrimHint(55));
        Check.Equal("Trim it to about 1:05:00", ShareFitModel.TrimHint(3900), "an hour or more reads h:mm:ss");
        Check.Equal("A 3:20 clip can't fit in 20 MB. Trim it to about 0:55.", ShareFitModel.CantFitText(Free, 200, 55));
        Check.Equal("A 3:20 clip can't fit in 20 MB.", ShareFitModel.CantFitText(Free, 200, 0), "nothing to trim to");
        Check.Equal("Couldn't make a smaller copy: the disk is full. The original is 46 MB, over Discord free (20 MB).", ShareFitModel.FailedText(Free, "the disk is full", 46_000_000));
        Check.Equal("Couldn't make a smaller copy: the copy stayed too big. The original is 46 MB, over Discord free (20 MB). Trim it to about 0:30.",
            ShareFitModel.StayedTooBigText(Free, 46_000_000, 30));
        Check.Equal("Smaller copies are not available while Clink runs as administrator. The original is 46 MB, over Discord free (20 MB).",
            ShareFitModel.UnavailableText(Free, ShareCache.ElevatedReason, 46_000_000));
        Check.Equal("Too big for Discord free (20 MB): open Clink to make a smaller copy", ShareFitModel.GameRefusalText(Free));
        Check.Equal("Too big for Copium: open Clink to make a smaller copy", ShareFitModel.GameRefusalText(ShareFitModel.ForCopium(false)));
        Check.Equal("Too big for Copium Plus: open Clink to make a smaller copy", ShareFitModel.GameRefusalText(ShareFitModel.ForCopium(true)));
        Check.Equal("Making a smaller copy for Copium... 40%", ShareFitModel.WorkingText(ShareFitModel.ForCopium(false), DesignPlan(), 0.4));
        Check.Equal("2 files are over Discord free (20 MB): share them one at a time.", ShareFitModel.MultiFileNote(Free, 2));
        Check.Equal("3 files are over Discord Nitro Basic (50 MB): share them one at a time.", ShareFitModel.MultiFileNote(ShareFitModel.ForPlan("basic"), 3));
        Check.Null(ShareFitModel.MultiFileNote(Free, 1), "one file needs no note");
        Check.Null(ShareFitModel.MultiFileNote(Free, 0));
        Check.Equal("there is not enough free disk space (about 111 MB are needed)", ShareFitModel.LowDiskReason(20_000_000));
        Check.Equal(44_000_000L + 64L * 1024 * 1024, ShareFitModel.RequiredFreeBytes(20_000_000), "2.2 x the limit + 64 MB");
    }

    [UnitTest]
    public static void The_window_state_follows_the_clip_the_progress_and_the_result()
    {
        Check.Equal(ShareFitStateKind.Fits, ShareFitModel.StateFor(20_000_000, Free).Kind, "exactly the limit fits");
        Check.Equal(ShareFitStateKind.Over, ShareFitModel.StateFor(20_000_001, Free).Kind);
        Check.Equal(ShareFitStateKind.Fits, ShareFitModel.StateFor(99, new ShareFitTarget(0, 0, "none")).Kind, "no limit");

        Check.Equal(ShareFitStateKind.Waiting, ShareFitModel.StateFor(new ShareCopyProgress(ShareCopyStage.Waiting, 0, null)).Kind);
        var working = ShareFitModel.StateFor(new ShareCopyProgress(ShareCopyStage.Working, 0.42, DesignPlan()));
        Check.Equal(ShareFitStateKind.Working, working.Kind);
        Check.Near(0.42, working.Progress);
        Check.Equal("720p60", working.Plan!.Label);
        Check.Equal("Making a 20 MB copy for Discord: 720p60 at 3.1 Mbps... 42%", ShareFitModel.Text(working, Free, 46_000_000, 45));
        Check.Equal("Waiting...", ShareFitModel.Text(ShareFitState.Waiting, Free, 46_000_000, 45));
        Check.Null(ShareFitModel.Text(ShareFitState.Fits, Free, 1, 1), "a clip that fits says nothing");
        Check.Equal("Too big for Discord free (20 MB): this clip is 46 MB", ShareFitModel.Text(ShareFitState.Over, Free, 46_000_000, 45));

        var original = new ShareCopyResult(ShareCopyOutcome.UseOriginal, @"C:\a.mp4", 46_000_000) { OriginalBytes = 46_000_000 };
        Check.Equal(ShareFitStateKind.Fits, ShareFitModel.StateFor(original).Kind, "the original, no reason: it fits");
        var unavailable = ShareFitModel.StateFor(original with { ErrorKind = ShareCopyError.CacheUnavailable, Error = ShareCache.ElevatedReason });
        Check.Equal(ShareFitStateKind.Unavailable, unavailable.Kind);
        Check.Equal("Smaller copies are not available while Clink runs as administrator. The original is 46 MB, over Discord free (20 MB).", ShareFitModel.Text(unavailable, Free, 46_000_000, 45));

        var copy = new ShareCopyResult(ShareCopyOutcome.UseCopy, @"C:\cache\a.mp4", 19_100_000) { Label = "720p60", Seconds = 45 };
        var done = ShareFitModel.StateFor(copy);
        Check.Equal(ShareFitStateKind.Done, done.Kind);
        Check.True(ReferenceEquals(copy, done.Copy));
        Check.Equal("19.1 MB" + Dot + "0:45" + Dot + "720p60", ShareFitModel.Text(done, Free, 46_000_000, 45));

        var cant = ShareFitModel.StateFor(original with { Outcome = ShareCopyOutcome.CantFit, MaxSeconds = 55 });
        Check.Equal((ShareFitStateKind.CantFit, 55), (cant.Kind, cant.MaxSeconds));
        Check.Equal("A 3:20 clip can't fit in 20 MB. Trim it to about 0:55.", ShareFitModel.Text(cant, Free, 46_000_000, 200));

        var failed = ShareFitModel.StateFor(original with { Outcome = ShareCopyOutcome.Failed, Error = "the disk is full" });
        Check.Equal(ShareFitStateKind.Failed, failed.Kind);
        Check.Equal("Couldn't make a smaller copy: the disk is full. The original is 46 MB, over Discord free (20 MB).", ShareFitModel.Text(failed, Free, 46_000_000, 45));
        var tooBig = ShareFitModel.StateFor(original with { Outcome = ShareCopyOutcome.Failed, Error = ShareFitModel.StayedTooBigReason, MaxSeconds = 30 });
        Check.Equal("Couldn't make a smaller copy: the copy stayed too big. The original is 46 MB, over Discord free (20 MB). Trim it to about 0:30.",
            ShareFitModel.Text(tooBig, Free, 46_000_000, 45), "stayed too big is a failure with a trim hint, not a can't-fit");

        var refused = ShareFitModel.StateFor(original with { Outcome = ShareCopyOutcome.Refused, Error = ShareFitModel.GameRefusalText(Free) });
        Check.Equal(ShareFitStateKind.Refused, refused.Kind);
        Check.Equal("Too big for Discord free (20 MB): open Clink to make a smaller copy", ShareFitModel.Text(refused, Free, 46_000_000, 45));
        Check.Equal(ShareFitStateKind.Over, ShareFitModel.StateFor(original with { Outcome = ShareCopyOutcome.Cancelled }).Kind, "a cancel brings the button back");
    }
}
