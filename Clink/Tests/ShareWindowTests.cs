using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Clink.Engine;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;
using Rig = Clink.Tests.ShareCopyJobTests.Rig;
using Sink = Clink.Tests.ShareCopyJobTests.Sink;

namespace Clink.Tests;

/// <summary>
/// The smaller-copy parts of the UI (slice 2d-2): the SHARING card, the Share window's panel in every state, the window's lifecycle against
/// the real <c>AppController</c> job with a fake transcoder, and the note of the multi-file window. The windows are built and laid out but
/// never shown (no handle, nothing on screen); the UI thread is pumped for the answers.
/// </summary>
internal static class ShareWindowTests
{
    private const string Dot = " · ";
    private static ShareFitTarget Free => ShareFitModel.ForPlan("free");
    private static string OverText => ShareFitModel.OverText(Free, 46_000_000);

    private static void Pump(Func<bool> done, string what) => ScreenshotUploadTests.Pump(done, what);

    private static bool Shown(UIElement e) => e.Visibility == Visibility.Visible;

    /// <summary>Which of the panel's buttons are visible: "make", "cancel", "original" (in that order), comma separated; "" for none.</summary>
    private static string Buttons(ShareWindow w) => string.Join(",", new[] { (w.MakeCopyButton, "make"), (w.CancelCopyButton, "cancel"), (w.UseOriginalButton, "original") }
        .Where(b => Shown(b.Item1)).Select(b => b.Item2));

    /// <summary>A 46 MB 1080p60 clip of 45 s (the design document's example) whose copy comes out at 18 MB.</summary>
    private static Rig NewRig(Func<bool>? elevated = null)
    {
        var rig = new Rig(elevated);
        rig.Fake.ProbeFor = _ => new ShareProbeResult(45, "h264", 1920, 1080, 60, 1, 1.0);
        rig.Fake.TranscodeSize = _ => 18_000_000;
        rig.Fake.OutSeconds = 45;
        return rig;
    }

    private static ShareWindow Open(ClipEntry clip)
    {
        var w = new ShareWindow(clip);
        w.RefreshFit(comingForward: true);
        return w;
    }

    /// <summary>Runs the UI thread for a while: anything queued for it has run afterwards.</summary>
    private static void Settled(int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); Pump(() => sw.ElapsedMilliseconds >= ms, "a pause"); }

    // The window's target is the free plan's 20 MB (the rig's own target is 2 MB, for the job tests).
    private static string KeyOf(ClipEntry c) => ShareCache.KeyFor(c.Path, ShareFitModel.ForPlan("free").LimitBytes, ShareFitModel.Codec, ShareFitModel.CacheVariant);
    private static string FinalOf(Rig rig, ClipEntry c) => rig.Cache.PathFor(KeyOf(c), Path.GetFileName(c.Path));
    private static string PartOf(Rig rig, ClipEntry c) => rig.Cache.PartPathFor(KeyOf(c), Path.GetFileName(c.Path));

    private static void Settle(ShareWindow w) => Pump(() => w.FitIdle, "the window's look for a copy");

    // ------------------------------------------------------------------ the states

    [UnitTest]
    public static void The_panel_walks_through_over_preparing_working_waiting_and_done_and_the_card_follows_the_copy()
    {
        using var rig = NewRig();
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var clip2 = rig.Clip("other.mp4", 47_000_000, 45);
        var w = new ShareWindow(clip);
        Check.Equal(Visibility.Collapsed, w.FitPanel.Visibility, "nothing before the first look");

        // Over: the text, the button, no auto start.
        w.RefreshFit(comingForward: true);
        Settle(w);
        Check.Equal(ShareFitStateKind.Over, w.FitKind);
        Check.Equal(Visibility.Visible, w.FitPanel.Visibility);
        Check.Equal(OverText, w.FitText.Text);
        Check.Equal("make", Buttons(w));
        Check.Equal(ShareFitModel.MakeCopyText(Free), (string)w.MakeCopyButton.Content);
        Check.False(Shown(w.FitProgress), "no bar");
        Check.Equal(clip.Path, w.SharePath);
        Check.Equal(0, rig.Fake.Probes + rig.Fake.Transcodes, "nothing started on its own");
        Check.Equal("46 MB" + Dot + "0:45", w.MetaText.Text);

        // Preparing: at once after the click, before anything is reported.
        w.MakeCopy();
        Check.Equal(ShareFitStateKind.Preparing, w.FitKind);
        Check.Equal(ShareFitModel.PreparingText, w.FitText.Text);
        Check.Equal("cancel", Buttons(w));
        Check.True(Shown(w.FitProgress) && w.FitProgress.IsIndeterminate, "a busy bar");

        // Working: the plan and the percent.
        Pump(() => w.FitText.Text.EndsWith("... 50%"), "the copy to be at 50%");
        Check.Equal(ShareFitStateKind.Working, w.FitKind);
        Check.True(w.FitText.Text.StartsWith("Making a 20 MB copy for Discord: 720p60 at 3."), w.FitText.Text);
        Check.Contains(w.FitText.Text, " Mbps... 50%");
        Check.Equal("cancel", Buttons(w));
        Check.True(Shown(w.FitProgress) && !w.FitProgress.IsIndeterminate, "a determinate bar");
        Check.Near(0.5, w.FitProgress.Value, 1e-9);
        Check.Equal("Smaller copy progress", AutomationProperties.GetName(w.FitProgress), "a fixed name: the bar reports its own value");
        Check.Equal(clip.Path, w.SharePath, "still the original");

        // Waiting: a second clip's window while the first copy holds the slot.
        var w2 = Open(clip2);
        Settle(w2);
        w2.MakeCopy();
        Pump(() => w2.FitKind == ShareFitStateKind.Waiting, "the second copy to wait");
        Check.Equal(ShareFitModel.WaitingText, w2.FitText.Text);
        Check.Equal("cancel", Buttons(w2));
        Check.True(w2.FitProgress.IsIndeterminate);

        // Done: the card, the drag and Copy file use the copy.
        gate.SetResult();
        Pump(() => w.FitKind == ShareFitStateKind.Done && w2.FitKind == ShareFitStateKind.Done, "both copies");
        string details = (18.0).ToString("0.0", L.FormatCulture) + " MB" + Dot + "0:45" + Dot + "720p60";
        Check.Equal(details, w.FitText.Text);
        Check.Equal(details, w.MetaText.Text, "the card describes the file that is shared");
        Check.Equal("original", Buttons(w));
        Check.False(Shown(w.FitProgress));
        Check.NotEqual(clip.Path, w.SharePath);
        Check.Equal(FinalOf(rig, clip), w.SharePath);
        Check.True(w.SharePath.EndsWith("big.mp4", StringComparison.OrdinalIgnoreCase), "the original name");
        Check.Equal(new[] { w.SharePath }, ShareWindow.FileData(w.SharePath).GetData(DataFormats.FileDrop) as string[], "what a drag carries");
        Check.Equal(clip.Path, w.Entry.Path, "Open folder shows the original");
        Check.Equal(ShareFitModel.UseOriginalText, (string)w.UseOriginalButton.Content);
        Check.Equal("Use the original clip", AutomationProperties.GetName(w.UseOriginalButton));
        Check.True(rig.Cache.IsInUse(KeyOf(clip)), "the copy is in use while the window shows it");

        // Use the original: back to the clip, the button to make (find) the copy returns, and a look later does not take the card back.
        w.UseOriginal();
        Check.Equal(clip.Path, w.SharePath);
        Check.Equal(ShareFitStateKind.Over, w.FitKind);
        Check.Equal("make", Buttons(w));
        Check.Equal("46 MB" + Dot + "0:45", w.MetaText.Text);
        Check.False(rig.Cache.IsInUse(KeyOf(clip)), "the window let go of the copy");
        w.RefreshFit(comingForward: true);
        Settle(w);
        Check.Equal(ShareFitStateKind.Over, w.FitKind);
        Check.Equal(clip.Path, w.SharePath);
        int made = rig.Fake.Transcodes;
        w.MakeCopy();
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the copy again");
        Check.Equal(made, rig.Fake.Transcodes, "the copy was in the cache: nothing made again");
        Check.Equal(FinalOf(rig, clip), w.SharePath);
    }

    [UnitTest]
    public static void A_clip_that_fits_the_plan_shows_no_panel_and_a_changed_plan_is_looked_at_again()
    {
        using var rig = NewRig();
        var small = rig.Clip("small.mp4", 15_000_000, 20);
        var w = Open(small);
        Settle(w);
        Check.Equal(ShareFitStateKind.Fits, w.FitKind);
        Check.Equal(Visibility.Collapsed, w.FitPanel.Visibility);
        Check.Equal("", Buttons(w));
        Check.Equal(0, rig.Fake.Probes, "a clip under the limit is never looked at");

        var big = rig.Clip("big.mp4", 46_000_000, 45);
        rig.S.DiscordPlan = "basic";
        var w2 = Open(big);
        Settle(w2);
        Check.Equal(ShareFitStateKind.Fits, w2.FitKind, "46 MB is within Nitro Basic's 50 MB");
        Check.Equal(Visibility.Collapsed, w2.FitPanel.Visibility);
        Check.Equal(50_000_000L, w2.FitTarget.LimitBytes);

        rig.S.DiscordPlan = "free";
        w2.RefreshFit(comingForward: false);   // the controller's tick
        Settle(w2);
        Check.Equal(ShareFitStateKind.Over, w2.FitKind);
        Check.Equal(OverText, w2.FitText.Text);
        Check.Equal(20_000_000L, w2.FitTarget.LimitBytes);

        rig.S.DiscordPlan = "nitro";
        w2.RefreshFit(comingForward: false);
        Check.Equal(ShareFitStateKind.Fits, w2.FitKind);
        Check.Equal("Discord Nitro (1 GB)", w2.FitTarget.Label);
        Check.Equal(0, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void A_plan_change_while_a_copy_is_made_leaves_the_copy_running_and_finds_it_again_when_the_plan_comes_back()
    {
        using var rig = NewRig();
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitText.Text.EndsWith("... 50%"), "the copy under way");
        rig.S.DiscordPlan = "basic";
        w.RefreshFit(comingForward: false);
        Check.Equal(ShareFitStateKind.Fits, w.FitKind, "the copy is not needed for the 50 MB plan");
        Check.False(rig.Fake.SawCancel, "the window leaving does not cancel it");
        rig.S.DiscordPlan = "free";
        w.RefreshFit(comingForward: false);
        Pump(() => w.FitKind == ShareFitStateKind.Working && w.FitText.Text.EndsWith("... 50%"), "the window to join the running copy again");
        gate.SetResult();
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the copy");
        Check.Equal(1, rig.Fake.Transcodes);
        Check.Equal(FinalOf(rig, clip), w.SharePath);
    }

    [UnitTest]
    public static void A_clip_that_cannot_fit_says_how_long_to_trim_it_to_and_the_original_stays_the_one_shared()
    {
        using var rig = NewRig();
        rig.Fake.ProbeFor = _ => new ShareProbeResult(200, "h264", 1920, 1080, 60, 1, 1.0);
        var clip = rig.Clip("long.mp4", 400_000_000, 200);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitKind == ShareFitStateKind.CantFit, "the answer");
        Check.True(w.FitText.Text.StartsWith("A 3:20 clip can't fit in 20 MB. Trim it to about "), w.FitText.Text);
        Check.Equal("", Buttons(w));
        Check.False(Shown(w.FitProgress));
        Check.Equal(clip.Path, w.SharePath);
        Check.Equal(0, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void A_failed_copy_says_why_names_the_original_and_sends_nothing()
    {
        using var rig = NewRig();
        rig.App.FreeSpaceProbe = _ => 0;
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitKind == ShareFitStateKind.Failed, "the failure");
        Check.True(w.FitText.Text.StartsWith("Couldn't make a smaller copy: there is not enough free disk space"), w.FitText.Text);
        Check.True(w.FitText.Text.EndsWith(" The original is 46 MB, over Discord free (20 MB)."), w.FitText.Text);
        Check.Equal("make", Buttons(w), "a failure another try may cure offers the button again");
        Check.Equal(clip.Path, w.SharePath, "the original is not sent on its own: it is only what the card holds");
        Check.Equal(0, rig.Fake.Transcodes);

        // retry after the disk failure: space came back
        rig.App.FreeSpaceProbe = _ => long.MaxValue;
        w.MakeCopy();
        Check.Equal(ShareFitStateKind.Preparing, w.FitKind, "the click works from Failed");
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the retry");
        Check.Equal(FinalOf(rig, clip), w.SharePath);
        Check.Equal(1, rig.Fake.Transcodes);

        // a transcoder that fails
        rig.Fake.OnProbe = _ => throw new ShareTranscodeException(ShareTranscodeFailure.DecoderMissing, "Windows has no decoder for this clip");
        var clip2 = rig.Clip("hevc.mp4", 46_000_000, 45);
        var w2 = Open(clip2);
        Settle(w2);
        w2.MakeCopy();
        Pump(() => w2.FitKind == ShareFitStateKind.Failed, "the second failure");
        Check.Equal("Couldn't make a smaller copy: Windows has no decoder for this clip. The original is 46 MB, over Discord free (20 MB).", w2.FitText.Text);
    }

    [UnitTest]
    public static void An_elevated_Clink_shows_the_reason_at_once_and_makes_nothing()
    {
        using var rig = NewRig(elevated: () => true);
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        Check.Equal(ShareFitStateKind.Unavailable, w.FitKind);
        Check.Equal("Smaller copies are not available while Clink runs as administrator. The original is 46 MB, over Discord free (20 MB).", w.FitText.Text);
        Check.Equal("", Buttons(w), "nothing to press: the original is shared");
        Check.Equal(clip.Path, w.SharePath);
        Check.Equal(0, rig.Fake.Probes + rig.Fake.Transcodes);
        Check.False(Directory.Exists(rig.Cache.Root), "and the cache was not touched");
    }

    [UnitTest]
    public static void A_game_in_front_with_copies_during_games_off_shows_the_game_text()
    {
        using var rig = NewRig();
        rig.S.ShareCopiesDuringGames = false;
        rig.App.GameOrFullScreenProbe = () => true;
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitKind == ShareFitStateKind.Refused, "the refusal");
        Check.Equal(ShareFitModel.GameRefusalText(Free), w.FitText.Text);
        Check.Equal("make", Buttons(w), "the refusal is not a dead end: asking again works once the game is gone");
        Check.Equal(clip.Path, w.SharePath);
        Check.Equal(0, rig.Fake.Transcodes);

        // retry after the refusal (the game left)
        rig.App.GameOrFullScreenProbe = () => false;
        w.MakeCopy();
        Check.Equal(ShareFitStateKind.Preparing, w.FitKind);
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the retry");
        Check.Equal(FinalOf(rig, clip), w.SharePath);
    }

    [UnitTest]
    public static void A_Make_that_waits_for_the_slot_and_meets_a_game_is_refused_and_can_be_asked_again()
    {
        using var rig = NewRig();
        rig.S.ShareCopiesDuringGames = false;
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var first = rig.Clip("first.mp4", 46_000_000, 45);
        var second = rig.Clip("second.mp4", 47_000_000, 45);
        var w1 = Open(first);
        Settle(w1);
        w1.MakeCopy();
        Pump(() => w1.FitText.Text.EndsWith("... 50%"), "the first copy to hold the slot");
        var w2 = Open(second);
        Settle(w2);
        w2.MakeCopy();
        Pump(() => w2.FitKind == ShareFitStateKind.Waiting, "the second to wait");
        rig.App.GameOrFullScreenProbe = () => true;   // a game comes forward while it waits
        gate.SetResult();
        Pump(() => w2.FitKind == ShareFitStateKind.Refused, "the refusal after the wait");
        Check.Equal("make", Buttons(w2));
        rig.App.GameOrFullScreenProbe = () => false;
        w2.MakeCopy();
        Pump(() => w2.FitKind == ShareFitStateKind.Done, "the retry");
    }

    [UnitTest]
    public static void A_copy_that_stayed_too_big_offers_no_button_because_another_try_gives_the_same()
    {
        using var rig = NewRig();
        rig.Fake.TranscodeSize = _ => 30_000_000;   // every attempt comes out over 20 MB
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitKind == ShareFitStateKind.Failed, "the failure");
        Check.Contains(w.FitText.Text, "the copy stayed too big");
        Check.Equal("", Buttons(w), "no button: trim the clip instead");
        int made = rig.Fake.Transcodes;
        w.MakeCopy();   // and a click that arrives anyway does nothing
        Check.Equal(ShareFitStateKind.Failed, w.FitKind);
        Settled(80);
        Check.Equal(made, rig.Fake.Transcodes);
        Check.Equal(clip.Path, w.SharePath);
    }

    [UnitTest]
    public static void The_panel_texts_are_announced_politely_and_the_buttons_and_bar_have_names()
    {
        using var rig = NewRig();
        var w = new ShareWindow(rig.Clip("big.mp4", 46_000_000, 45));
        Check.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(w.FitText));
        Check.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(w.StatusText));
        Check.Equal("Smaller copy progress", AutomationProperties.GetName(w.FitProgress));
        Check.Equal("Cancel the smaller copy", AutomationProperties.GetName(w.CancelCopyButton));
        Check.Equal("Use the original clip", AutomationProperties.GetName(w.UseOriginalButton));
        Check.Equal(ShareFitModel.CancelText, (string)w.CancelCopyButton.Content);
    }

    [UnitTest]
    public static void The_panel_and_status_texts_are_announced_when_the_kind_of_state_changes_not_on_every_progress_tick()
    {
        using var rig = NewRig();
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = new ShareWindow(clip);
        Check.Equal(0, w.FitAnnouncements);
        w.RefreshFit(comingForward: true);
        Settle(w);
        Check.Equal(1, w.FitAnnouncements, "Fits -> Over");
        w.MakeCopy();
        Check.Equal(2, w.FitAnnouncements, "Over -> Preparing");
        Pump(() => w.FitKind == ShareFitStateKind.Working && w.FitText.Text.EndsWith("... 50%"), "the copy under way");
        Check.Equal(3, w.FitAnnouncements, "Preparing -> Working");
        rig.Fake.LastProgress!.Report(0.6);
        Pump(() => w.FitText.Text.EndsWith("... 60%"), "a progress tick");
        rig.Fake.LastProgress!.Report(0.7);
        Pump(() => w.FitText.Text.EndsWith("... 70%"), "another progress tick");
        Check.Equal(3, w.FitAnnouncements, "progress ticks are not announced");
        gate.SetResult();
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the copy");
        Check.Equal(4, w.FitAnnouncements, "Working -> Done");

        int status = w.StatusAnnouncements;
        File.Delete(w.SharePath);
        Check.Null(w.SharePathNow());
        Check.Equal(status + 1, w.StatusAnnouncements, "the status line is announced when it gets text");
        Settle(w);
    }

    [UnitTest]
    public static void The_focus_goes_to_cancel_after_make_and_to_copy_file_after_done_and_the_names_stay_fixed()
    {
        using var rig = NewRig();
        var w = new ShareWindow(rig.Clip("big.mp4", 46_000_000, 45));
        Check.True(ReferenceEquals(w.CancelCopyButton, w.FocusFor(ShareFitStateKind.Preparing, ShareFitState.Preparing)), "Make pressed");
        Check.True(ReferenceEquals(w.CancelCopyButton, w.FocusFor(ShareFitStateKind.Working, ShareFitState.Working(0.2, null))));
        Check.True(ReferenceEquals(w.CopyButton, w.FocusFor(ShareFitStateKind.Done, ShareFitState.Over)), "Done: Copy file, the primary button");
        Check.True(ReferenceEquals(w.MakeCopyButton, w.FocusFor(ShareFitStateKind.Over, ShareFitState.Over)), "Cancel pressed: Make is back");
        Check.True(ReferenceEquals(w.CopyButton, w.FocusFor(ShareFitStateKind.Failed, ShareFitState.Failed(ShareFitModel.StayedTooBigReason, ShareCopyError.StayedTooBig))), "no Make button: Copy file");
        w.CopyButton.IsEnabled = false;
        Check.True(ReferenceEquals(w.UseOriginalButton, w.FocusFor(ShareFitStateKind.Done, ShareFitState.Over)), "Copy file is off: Use the original");
    }

    [UnitTest]
    public static void A_smaller_copy_that_was_cleaned_up_says_so_and_the_panel_looks_again()
    {
        using var rig = NewRig();
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the copy");
        string copy = w.SharePath;
        Check.Equal(copy, w.SharePathNow(), "while it is there, it is the path");
        File.Delete(copy);
        Check.Null(w.SharePathNow(), "gone: nothing to share");
        Check.Equal("The smaller copy is not there any more. Clink looks again.", w.StatusText.Text);
        Check.Equal(clip.Path, w.SharePath, "the card is the original again");
        Settle(w);
        Check.Equal(ShareFitStateKind.Over, w.FitKind, "looked again: no copy, the button is back");
        Check.Equal("make", Buttons(w));
        Check.False(rig.Cache.IsInUse(KeyOf(clip)), "the lost copy's mark was released");
    }

    [UnitTest]
    public static void A_plan_change_while_the_copy_is_in_use_hands_the_card_back_to_the_original_and_back_again_on_the_tick()
    {
        using var rig = NewRig();
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the copy");
        Check.Equal(FinalOf(rig, clip), w.SharePath);

        rig.S.DiscordPlan = "basic";
        w.OnStateTick();   // what the controller's once-a-second tick runs
        Check.Equal(ShareFitStateKind.Fits, w.FitKind, "46 MB fits the 50 MB plan: the copy is not needed");
        Check.Equal(clip.Path, w.SharePath);
        Check.Equal("46 MB" + Dot + "0:45", w.MetaText.Text);
        Check.Equal(Visibility.Collapsed, w.FitPanel.Visibility);
        Check.False(rig.Cache.IsInUse(KeyOf(clip)), "the copy is let go of");

        rig.S.DiscordPlan = "free";
        w.OnStateTick();
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the copy to be found again");
        Check.Equal(FinalOf(rig, clip), w.SharePath);
        Check.Equal(1, rig.Fake.Transcodes, "found, not made again");
    }

    [UnitTest]
    public static void The_controllers_tick_reaches_an_open_share_window_and_stops_when_it_closes()
    {
        using var rig = NewRig();
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = new ShareWindow(clip);
        new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();   // SourceInitialized: the window subscribes (it is never shown)
        var field = typeof(AppController).GetField("StateChanged", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("AppController.StateChanged is gone");
        bool Subscribed() => ((Action?)field.GetValue(rig.App))?.GetInvocationList().Any(d => ReferenceEquals(d.Target, w)) == true;
        Check.True(Subscribed(), "subscribed to the controller's tick");
        rig.S.DiscordPlan = "free";
        ((Action?)field.GetValue(rig.App))!.Invoke();
        Pump(() => w.FitKind == ShareFitStateKind.Over, "the tick to make the window look at the clip");
        rig.S.DiscordPlan = "nitro";
        ((Action?)field.GetValue(rig.App))!.Invoke();
        Check.Equal(ShareFitStateKind.Fits, w.FitKind, "a changed plan reaches the open window on the next tick");
        w.Close();
        Check.False(Subscribed(), "and the window lets go of the tick when it closes");
    }

    // ------------------------------------------------------------------ the lifecycle

    [UnitTest]
    public static void A_window_that_opens_while_the_copy_is_being_made_joins_it_without_a_click()
    {
        using var rig = NewRig();
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var maker = new Sink();
        var make = Task.Run(() => rig.App.MakeShareCopyAsync(clip, ShareFitModel.ForPlan("free"), maker, default));
        ShareCopyJobTests.Spin(() => maker.Seen.Any(p => p.Fraction >= 0.5), "the copy to be at 50%");
        var w = Open(clip);
        Pump(() => w.FitKind == ShareFitStateKind.Working && w.FitText.Text.EndsWith("... 50%"), "the window to be told where the job is");
        Check.Equal("cancel", Buttons(w));
        Check.Equal(1, rig.Fake.Transcodes, "opening never starts a copy");
        gate.SetResult();
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the window to show the copy");
        using var r = ShareCopyJobTests.Wait(make);
        Check.Equal(r.Path, w.SharePath);
        Check.Equal(1, rig.Fake.Transcodes);
    }

    [UnitTest]
    public static void Closing_the_window_abandons_its_ask_and_the_copy_finishes_in_the_background_and_is_there_next_time()
    {
        using var rig = NewRig();
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitText.Text.EndsWith("... 50%"), "the copy under way");
        string key = KeyOf(clip);
        Check.True(rig.Cache.IsInUse(key));
        string before = w.FitText.Text;
        w.Close();
        gate.SetResult();
        Pump(() => File.Exists(FinalOf(rig, clip)), "the copy to finish with the window gone");
        Pump(() => !rig.Cache.IsInUse(key), "the marks to be released: the window's at once, the job's when it ends");
        Check.False(rig.Fake.SawCancel, "closing is not a cancel");
        Check.Equal(1, rig.Fake.Transcodes);
        Settled(80);
        Check.Equal(before, w.FitText.Text, "a late answer or report is ignored by a closed window");
        Check.Equal(ShareFitStateKind.Working, w.FitKind);
        Check.False(File.Exists(PartOf(rig, clip)), "no part file left");

        // The next window finds it (the copy's own quality is read from the file: 720p60).
        rig.Fake.ProbeFor = p => p.StartsWith(rig.Cache.Root, StringComparison.OrdinalIgnoreCase)
            ? new ShareProbeResult(45, "h264", 1280, 720, 60, 1, 1.0) : new ShareProbeResult(45, "h264", 1920, 1080, 60, 1, 1.0);
        var again = Open(clip);
        Pump(() => again.FitKind == ShareFitStateKind.Done, "the copy to be found");
        Check.Equal(FinalOf(rig, clip), again.SharePath);
        Check.Equal(1, rig.Fake.Transcodes, "found, not made again");
        Check.Equal((18.0).ToString("0.0", L.FormatCulture) + " MB" + Dot + "0:45" + Dot + "720p60", again.FitText.Text);
        again.Close();
        Pump(() => !rig.Cache.IsInUse(key), "the second window to let go");
    }

    [UnitTest]
    public static void Cancel_stops_the_copy_and_a_fresh_token_lets_the_next_click_work()
    {
        using var rig = NewRig();
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip("big.mp4", 46_000_000, 45);
        var w = Open(clip);
        Settle(w);
        w.MakeCopy();
        Pump(() => w.FitText.Text.EndsWith("... 50%"), "the copy under way");
        w.CancelCopy();
        Check.Equal(ShareFitStateKind.Over, w.FitKind, "the button comes back at once");
        Check.Equal("make", Buttons(w));
        Check.Equal(OverText, w.FitText.Text);
        ShareCopyJobTests.Spin(() => rig.Fake.SawCancel, "the job to see the cancel");

        rig.Fake.Hold = null;
        w.MakeCopy();   // waits for the cancelled job to clean up, then starts afresh on the new token
        Pump(() => w.FitKind == ShareFitStateKind.Done, "the second copy");
        Check.Equal(2, rig.Fake.Transcodes);
        Check.Equal(FinalOf(rig, clip), w.SharePath);
        Check.False(File.Exists(PartOf(rig, clip)), "no part left");
        Settled(80);
        Check.Equal(ShareFitStateKind.Done, w.FitKind, "the cancelled ask's late answer changed nothing");
    }

    // ------------------------------------------------------------------ F1: no catch-up after the asker left

    [UnitTest]
    public static void An_asker_that_leaves_while_its_catch_up_is_being_delivered_gets_nothing_that_was_queued_behind_it()
    {
        using var rig = NewRig();
        rig.Fake.ProbeFor = _ => new ShareProbeResult(5, "h264", 1920, 1080, 60, 1, 1.0);
        var gate = new TaskCompletionSource();
        rig.Fake.Hold = (_, ct) => gate.Task.WaitAsync(ct);
        var clip = rig.Clip("f1.mp4", 6_000_000, 5);
        var target = new ShareFitTarget(2_000_000, 0, "Discord test (2 MB)");
        var maker = new Sink();
        var make = rig.App.MakeShareCopyAsync(clip, target, maker, default);
        ShareCopyJobTests.Spin(() => maker.Seen.Any(p => p.Fraction >= 0.5), "the job to be at 0.5");

        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var finder = new Sink { Before = n => { if (n == 1) { entered.Set(); release.Wait(10000); } } };
        var find = rig.App.FindShareCopyAsync(clip, target, finder);
        Check.True(entered.Wait(10000), "the catch-up to be under way (held in the sink)");

        rig.Fake.LastProgress!.Report(0.75);   // the job reports more: queued behind the catch-up that is still being delivered
        // The asker leaves on its own thread (not the pool: a busy pool must not delay it). The hook fires on that thread once the listening
        // is disposed (flag set), just before it waits for the call in flight; so the order below holds whatever the timing.
        var disposed = new ManualResetEventSlim();
        var left = new ManualResetEventSlim();
        AppController.ListeningDisposedHook = s => { if (ReferenceEquals(s, finder)) disposed.Set(); };
        try
        {
            var leaver = new System.Threading.Thread(() => { ShareCopyResult.Abandon(find); left.Set(); }) { IsBackground = true, Name = "f1-leaver" };
            leaver.Start();
            Check.True(disposed.Wait(10000), "the asker's listening to be disposed");
            Check.False(left.IsSet, "Abandon waits for the call in flight: nothing reaches the sink once it has returned");
            release.Set();
            Check.True(left.Wait(10000), "and returns when that call is over");
            Check.True(leaver.Join(10000), "the leaving thread to end");
        }
        finally { AppController.ListeningDisposedHook = null; release.Set(); }
        System.Threading.Thread.Sleep(150);
        Check.Equal(1, finder.Seen.Length, "only the catch-up that was under way; what was queued behind it never reached a sink that had left");
        Check.True(finder.Seen[0].Fraction < 0.75, "and it is the catch-up");
        gate.SetResult();
        using var made = ShareCopyJobTests.Wait(make);
        using var found = ShareCopyJobTests.Wait(find);
        Check.Equal(1, finder.Seen.Length, "nor did the end of the job");
    }

    [UnitTest]
    public static void An_asker_that_waits_for_a_cancelled_job_is_told_it_waits()
    {
        using var rig = NewRig();
        rig.Fake.ProbeFor = _ => new ShareProbeResult(5, "h264", 1920, 1080, 60, 1, 1.0);   // 6 MB in 5 s fits 2 MB at 720p60
        rig.Fake.Hold = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        rig.Fake.CleanupDelayMs = 600;
        var clip = rig.Clip("f4.mp4", 6_000_000, 5);
        var target = new ShareFitTarget(2_000_000, 0, "Discord test (2 MB)");
        using var cts = new CancellationTokenSource();
        var first = Task.Run(() => rig.App.MakeShareCopyAsync(clip, target, null, cts.Token));
        ShareCopyJobTests.Spin(() => rig.Fake.Transcodes == 1, "the transcode to be under way");
        cts.Cancel();
        ShareCopyJobTests.Spin(() => rig.Fake.SawCancel, "the job to see the cancel");
        var sink = new Sink();
        using var cts2 = new CancellationTokenSource();
        var second = Task.Run(() => rig.App.MakeShareCopyAsync(clip, target, sink, cts2.Token));
        ShareCopyJobTests.Spin(() => sink.Seen.Length > 0, "Waiting to be reported before the wait");
        Check.Equal(ShareCopyStage.Waiting, sink.Seen[0].Stage);
        Check.False(second.IsCompleted, "while the cancelled job is still cleaning up");
        cts2.Cancel();
        using var r2 = ShareCopyJobTests.Wait(second);
        using var r1 = ShareCopyJobTests.Wait(first);
        Check.Equal(ShareCopyOutcome.Cancelled, r2.Outcome);
    }

    // ------------------------------------------------------------------ the SHARING card

    private static AppController NewApp(TempDir dir, Settings s)
    {
        Settings.DirOverride = dir.Path;
        s.ClipsFolder = dir.File("clips");
        Directory.CreateDirectory(s.ClipsFolder);
        return new AppController(s, new Updater(s));
    }

    private static void SetLoaded(MainWindow w)
    {
        var field = typeof(MainWindow).GetField("_loading", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new TestFailure("MainWindow._loading is gone");
        field.SetValue(w, false);
    }

    [UnitTest]
    public static void The_Sharing_card_offers_the_three_plans_with_the_exact_labels_and_starts_on_the_saved_one()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("{}");
        using var app = NewApp(dir, s);
        var w = new MainWindow();
        w.FillSharingCard(s);
        Check.Equal(new[] { "Discord free (20 MB)", "Discord Nitro Basic (50 MB)", "Discord Nitro (1 GB)" },
            w.DiscordPlanCombo.Items.Cast<Choice>().Select(c => c.Label).ToArray());
        Check.Equal(new object[] { "free", "basic", "nitro" }, w.DiscordPlanCombo.Items.Cast<Choice>().Select(c => c.Value).ToArray());
        Check.Equal("free", ((Choice)w.DiscordPlanCombo.SelectedItem).Value, "new settings start on the free plan");
        Check.True(w.ShareCopiesDuringGamesCheck.IsChecked == true, "the toggle is on by default");
        Check.Equal("Make smaller copies during games", (string)w.ShareCopiesDuringGamesCheck.Content);
        Check.Equal("My Discord plan", AutomationProperties.GetName(w.DiscordPlanCombo));

        // a file that never had the setting takes it from the recording size target
        var legacy = Settings.FromJson("""{ "SizeTarget": "discord500" }""");
        w.FillSharingCard(legacy);
        Check.Equal("nitro", ((Choice)w.DiscordPlanCombo.SelectedItem).Value);
        var off = Settings.FromJson("""{ "ShareCopiesDuringGames": false, "DiscordPlan": "basic" }""");
        w.FillSharingCard(off);
        Check.Equal("basic", ((Choice)w.DiscordPlanCombo.SelectedItem).Value);
        Check.True(w.ShareCopiesDuringGamesCheck.IsChecked == false);
    }

    [UnitTest]
    public static void The_Sharing_card_writes_the_plan_and_the_toggle_to_the_settings_and_they_survive_a_reload()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("{}");
        using var app = NewApp(dir, s);
        var w = new MainWindow();
        w.FillSharingCard(s);
        SetLoaded(w);

        w.DiscordPlanCombo.SelectedIndex = 1;
        Check.Equal("basic", s.DiscordPlan);
        w.DiscordPlanCombo.SelectedIndex = 2;
        Check.Equal("nitro", s.DiscordPlan);
        w.ShareCopiesDuringGamesCheck.IsChecked = false;
        Check.False(s.ShareCopiesDuringGames);
        var reloaded = Settings.Load();
        Check.Equal("nitro", reloaded.DiscordPlan, "saved");
        Check.False(reloaded.ShareCopiesDuringGames, "saved");
        Check.Equal(1_000_000_000L, ShareFitModel.ForSettings(reloaded).LimitBytes);

        // filling the card is not a choice: nothing is written
        s.DiscordPlan = "free";
        w.FillSharingCard(s);
        Check.Equal("free", s.DiscordPlan);
        w.ShareCopiesDuringGamesCheck.IsChecked = true;
        Check.True(s.ShareCopiesDuringGames);
        Check.True(Settings.Load().ShareCopiesDuringGames);
    }

    // ------------------------------------------------------------------ the multi-file window

    private static ShareFileItem Item(TempDir dir, string name, long size) => new(dir.File(name), false, size, 30);

    [UnitTest]
    public static void The_multi_file_window_notes_how_many_files_are_over_the_plan_and_makes_no_copies()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("{}");
        using var app = NewApp(dir, s);
        var two = new[] { Item(dir, "a.mp4", 46_000_000), Item(dir, "b.mp4", 25_000_000), Item(dir, "c.mp4", 5_000_000) };
        Check.Equal("2 files are over Discord free (20 MB): share them one at a time.", new ShareFilesWindow(two, false).OverNoteText);
        var three = two.Append(Item(dir, "d.mp4", 21_000_000)).ToArray();
        Check.Equal("3 files are over Discord free (20 MB): share them one at a time.", new ShareFilesWindow(three, false).OverNoteText, "the count is right");
        Check.Equal("1 file is over Discord free (20 MB): share it on its own.", new ShareFilesWindow(new[] { two[0], two[2] }, false).OverNoteText, "one file over: a note too");
        Check.Equal("1 file is over Discord free (20 MB): share it on its own.", new ShareFilesWindow(new[] { two[0] }, false).OverNoteText, "a single file");
        Check.Null(new ShareFilesWindow(new[] { two[0] }, true).OverNoteText, "a screenshot is never over");
        Check.Null(new ShareFilesWindow(new[] { two[2], Item(dir, "e.mp4", 3_000_000) }, false).OverNoteText, "none over");
        Check.Null(new ShareFilesWindow(two, true).OverNoteText, "screenshots are small and are not clips");
        Check.Equal(Visibility.Collapsed, new ShareFilesWindow(two[2..], false).OverNote.Visibility);
        Check.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(new ShareFilesWindow(two, false).OverNote));

        s.DiscordPlan = "basic";
        Check.Null(new ShareFilesWindow(two, false).OverNoteText, "46 MB and 25 MB are both within 50 MB");
        s.DiscordPlan = "free";
        Check.Null(ShareFilesWindow.OverNoteFor(new[] { Item(dir, "x.mp4", 20_000_000), Item(dir, "y.mp4", 20_000_000) }, false, ShareFitModel.ForPlan("free")), "exactly the limit fits");
        Check.Equal(0, app.Library.Entries.Count, "nothing was made or added");
    }

    [UnitTest]
    public static void The_multi_file_note_follows_a_plan_change_on_the_tick_and_is_announced_only_when_its_text_changes()
    {
        using var dir = new TempDir();
        var s = Settings.FromJson("{}");
        using var app = NewApp(dir, s);
        var items = new[] { Item(dir, "a.mp4", 46_000_000), Item(dir, "b.mp4", 25_000_000) };
        var w = new ShareFilesWindow(items, false);
        string two = "2 files are over Discord free (20 MB): share them one at a time.";
        Check.Equal(two, w.OverNoteText);
        int n = w.OverNoteAnnouncements;
        Check.True(n >= 1, "announced when it first got text");
        w.OnStateTick();
        Check.Equal(n, w.OverNoteAnnouncements, "the same text is not announced again by the tick");

        s.DiscordPlan = "basic";   // 46 MB and 25 MB are both within 50 MB
        w.OnStateTick();
        Check.Null(w.OverNoteText, "the plan changed: the note follows within a tick");
        Check.Equal(n, w.OverNoteAnnouncements, "a note that went away is not announced");

        s.DiscordPlan = "free";
        w.OnStateTick();
        Check.Equal(two, w.OverNoteText);
        Check.Equal(n + 1, w.OverNoteAnnouncements, "new text is announced");

        var one = new[] { Item(dir, "c.mp4", 5_000_000), Item(dir, "d.mp4", 46_000_000) };
        var w1 = new ShareFilesWindow(one, false);
        int n1 = w1.OverNoteAnnouncements;
        s.DiscordPlan = "basic";
        w1.OnStateTick();
        Check.Null(w1.OverNoteText);
        s.DiscordPlan = "free";
        w1.OnStateTick();
        Check.Equal("1 file is over Discord free (20 MB): share it on its own.", w1.OverNoteText);
        Check.Equal(n1 + 1, w1.OverNoteAnnouncements);
    }
}
