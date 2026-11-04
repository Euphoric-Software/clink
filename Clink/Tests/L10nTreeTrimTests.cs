using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Clink.Engine;
using Clink.Playback;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Localization slice S5 (trim window, archive window and service, third-party notices window, the timeline, self-install, the recording
/// policy and the clip player's messages). Under <c>qps-ploc</c> the built (never shown) windows are walked by <see cref="L10nTreeHarness"/>:
/// every string must come from the catalog. Code-set texts carry the pseudo marker under qps and are today's English under en. Plural sites
/// are checked with synthetic ru / pl / ar catalogs.
/// </summary>
internal static class L10nTreeTrimTests
{
    // ------------------------------------------------------------------ rig

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static void RequireApp()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
    }

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Any)!.Invoke(target, args);

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Any)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, Any)!.SetValue(target, value);

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }

    /// <summary>Numbers and times are written in the user's regional format; the expected English text is the invariant one.</summary>
    private static IDisposable Invariant()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        return new Restore(() => CultureInfo.CurrentCulture = old);
    }

    private static void NoMissing(string what) => Check.Equal(Array.Empty<string>(), L.Missing.OrderBy(k => k, StringComparer.Ordinal).ToArray(), what + ": keys missing from the catalog");

    private static void Marked(string? text, Func<string, bool> allow, string what)
    {
        Check.True(!string.IsNullOrEmpty(text) && PseudoLocale.IsPseudo(text), what + ": not from the catalog: \"" + text + "\"");
        string rest = L10nTreeHarness.StripFrames(text!);
        Check.True(L10nTreeHarness.IsAllowed(rest) || allow(rest), what + ": has a hard-coded part: \"" + text + "\"");
    }

    private static void Marked(string? text, string what) => Marked(text, _ => false, what);

    private static void NoneUnlocalized(DependencyObject root, Func<string, bool> allow, string what)
    {
        var bad = L10nTreeHarness.Unlocalized(root, allow);
        Check.Equal(Array.Empty<string>(), bad.ToArray(), what + " (layout error: " + L10nTreeHarness.LastLayoutError + ")");
    }

    /// <summary>Texts that are not translatable on purpose, or that belong to another slice (named below). Each is commented.</summary>
    private static bool AllowTrim(string text)
    {
        string t = text.Trim();
        // the clip's file name (the player shows it as the title, the log and the status as a name): user data
        if (t is "source.mp4" or "source (trim).mp4" or "novoice.mp4") return true;
        // the label and the tips of the Share split button come from Services/ShareChoices.cs (slice S3b, not this one); each is listed here by its
        // exact text so a new hard-coded string next to them is still found
        if (t is "Share options" or "More share options") return true;
        return false;
    }

    /// <summary>The archive locations the tests type (user data, not translatable), each by its exact text; a test adds its own before it looks.</summary>
    private static readonly HashSet<string> s_archivePaths = new(StringComparer.OrdinalIgnoreCase) { "nas\\clips" };

    private static bool AllowArchive(string text) => s_archivePaths.Contains(text.Trim());

    // ------------------------------------------------------------------ the trim window

    [UnitTest]
    public static void The_trim_window_in_the_editor_and_in_the_player_is_localized()
    {
        RequireApp();
        foreach (var (token, cloud) in new[] { (true, true), (false, true), (true, false) })
        {
            using var rig = new TrimShareSplitTests.Rig(token, cloud);
            using var scope = L.Scope("qps-ploc");
            L.ClearMissing();
            using var win = new TrimShareSplitTests.Win(rig.Entry);
            var w = win.Window;
            string what = $"(token {token}, cloud {cloud})";
            NoneUnlocalized(w, AllowTrim, "the trim editor " + what);
            Marked(w.Title, "editor title");
            Marked((string)w.TrimToggle.Content, "the Trim switch");
            Marked((string)w.SaveButton.Content, "Save trimmed");
            Marked((string)w.SaveShareMain.Content, "Save & share (split)");
            Marked((string)w.PlayButton.ToolTip, "Play tip");
            Marked((string)w.SetInButton.ToolTip, "Set in tip");
            Marked((string)w.SetOutButton.ToolTip, "Set out tip");
            Marked(w.HintText.Text, "editor hint");
            Marked((string)w.PosText.ToolTip, "selection tip");
            Call(w, "SetTrimMode", false);
            NoneUnlocalized(w, AllowTrim, "the player " + what);
            Marked(w.HintText.Text, "player hint");
            Check.Null(w.PosText.ToolTip, "no selection tip in the player");
            NoMissing("trim window " + what);
        }
    }

    /// <summary>A headless player over a synthetic clip (opened on an MTA thread, as the other player tests do).</summary>
    private static ClipPlayer OpenHeadless(string path)
    {
        ClipPlayer? player = null;
        MixRenderTests.OnMta(() => player = new ClipPlayer(path, 0, headless: true));
        return player ?? throw new TestFailure("the synthetic clip could not be opened");
    }

    [UnitTest]
    public static void The_audio_track_rows_the_hints_and_the_mix_note_are_localized()
    {
        RequireApp();
        using var rig = new TrimShareSplitTests.Rig(false, true);
        string noVoice = rig.Dir.File("clips\\novoice.mp4");
        SyntheticClip.Write(noVoice, 3, voiceTrack: false);   // Mix, Game, Mic, Desktop: Voice is only inside the Mix
        var full = OpenHeadless(rig.Entry.Path);              // Mix, Game, Mic, Voice, Desktop
        var partial = OpenHeadless(noVoice);
        try
        {
            using var scope = L.Scope("qps-ploc");
            L.ClearMissing();
            foreach (var (player, entry) in new[] { (full, rig.Entry), (partial, rig.App.Library.Add(noVoice, 3, new FileInfo(noVoice).Length, null, Array.Empty<string>())) })
            {
                using var win = new TrimShareSplitTests.Win(entry);
                var w = win.Window;
                Call(w, "BuildTrackRows", player);
                string what = "track rows of " + Path.GetFileName(entry.Path);
                Check.True(w.TracksPanel.Children.Count == player.TrackCount, what + ": one row per track");
                NoneUnlocalized(w, AllowTrim, what + " (editor)");
                Marked(w.TracksHint.Text, "tracks hint (editor)");
                if (player == partial) { Check.True(w.MixNote.Visibility == Visibility.Visible, "Voice is only inside All audio: the note shows"); Marked(w.MixNote.Text, "mix note"); }
                Call(w, "SetTrimMode", false);
                NoneUnlocalized(w, AllowTrim, what + " (player)");
                Marked(w.TracksHint.Text, "tracks hint (player)");
                // an older Clink's clip: a source that could not be lined up with the Mix, one and several, either reason
                foreach (var (unlined, untrusted) in new[] { (new[] { TrackRole.Game }, false), (new[] { TrackRole.Game, TrackRole.Mic }, true), (new[] { TrackRole.Game, TrackRole.Mic, TrackRole.Voice }, false) })
                {
                    SetField(w, "_unlined", unlined);
                    SetField(w, "_untrusted", untrusted);
                    Call(w, "UpdateMixNote");
                    Check.True(w.MixNote.Visibility == Visibility.Visible, "the older-Clink note shows");
                    Marked(w.MixNote.Text, "older-Clink note " + string.Join("+", unlined));
                    NoneUnlocalized(w, AllowTrim, what + " with the older-Clink note");
                }
            }
            NoMissing("track rows");
        }
        finally { MixRenderTests.OnMta(() => { full.Dispose(); partial.Dispose(); }); }
    }

    [UnitTest]
    public static void English_texts_of_the_trim_window_are_what_they_were()
    {
        RequireApp();
        using var inv = Invariant();
        using var rig = new TrimShareSplitTests.Rig(true, true);
        string noVoice = rig.Dir.File("clips\\novoice.mp4");
        SyntheticClip.Write(noVoice, 3, voiceTrack: false);
        var full = OpenHeadless(rig.Entry.Path);
        var partial = OpenHeadless(noVoice);
        try
        {
            using var scope = L.Scope("en");
            using (var win = new TrimShareSplitTests.Win(rig.Entry))
            {
                var w = win.Window;
                Check.Equal("Trim · source.mp4", w.Title);
                Check.Equal("✂ Trim", (string)w.TrimToggle.Content);
                Check.Equal("Play / pause (Space)", (string)w.PlayButton.ToolTip);
                Check.Equal("Set the start to the playhead (I)", (string)w.SetInButton.ToolTip);
                Check.Equal("Set the end to the playhead (O)", (string)w.SetOutButton.ToolTip);
                Check.Equal("[ Set in", (string)w.SetInButton.Content);
                Check.Equal("Set out ]", (string)w.SetOutButton.Content);
                Check.Equal("Start snaps to keyframes · Space play · I/O set in/out · ←/→ frame step", w.HintText.Text);
                Check.Equal("Selection 0:00.0 → 0:06.0 (6.0s)", (string)w.PosText.ToolTip);
                Check.Equal("Save & share", (string)w.SaveShareMain.Content);
                Check.Equal("Replace original", (string)w.ReplaceCheck.Content);
                Check.Equal("Save trimmed", (string)w.SaveButton.Content);
                Check.Equal("100%", TrimWindow.PercentText(100));
                Check.Equal("38%", TrimWindow.PercentText(37.5), "rounded away from zero like the \"0\" format");
                Check.Equal("0%", TrimWindow.PercentText(0));
                Call(w, "BuildTrackRows", full);
                Check.Equal("Ticked tracks are saved at their volume · Listen picks what you hear · Game, Mic and Voice also shape All audio", w.TracksHint.Text);
                Check.Equal("Ticked tracks are saved at their volume · Listen picks what you hear · Game, Mic and Voice also shape All audio", (string)w.TracksHint.ToolTip);
                var boxes = w.TracksPanel.Children.OfType<Grid>().Select(g => g.Children.OfType<CheckBox>().Single()).ToList();
                Check.Equal(new[] { "All audio (mix)", "Game", "Mic", "Voice", "Desktop" }, boxes.Select(b => (string)b.Content).ToArray());
                Check.Equal("Ticked: Game is also saved as a track of its own, at the volume on the right. Unticked: no track of its own, but its volume still shapes All audio", (string)boxes[1].ToolTip);
                Check.Equal("Ticked: saved in the trimmed clip at the volume on the right. Unticked: left out of the file", (string)boxes[4].ToolTip);
                var radios = w.TracksPanel.Children.OfType<Grid>().Select(g => g.Children.OfType<RadioButton>().Single()).ToList();
                Check.Equal("Listen", (string)radios[0].Content);
                Check.Equal("Play All audio in the preview, with the Game, Mic and Voice volumes below applied. Players play one track at a time, usually this one", (string)radios[0].ToolTip);
                var sliders = w.TracksPanel.Children.OfType<Grid>().Select(g => g.Children.OfType<Slider>().Single()).ToList();
                Check.Equal("All audio's volume (right-click resets to 100%)", (string)sliders[0].ToolTip);
                Check.Equal("Game's volume, in its own track and inside All audio (right-click resets to 100%)", (string)sliders[1].ToolTip);
                Check.Equal("Volume in the saved clip (right-click resets to 100%)", (string)sliders[4].ToolTip);
                Check.True(w.MixNote.Visibility != Visibility.Visible, "all three sources are tracks: no note");
                Call(w, "SetTrimMode", false);
                Check.Equal("Listen picks what you hear · the Game, Mic and Voice sliders change All audio, live", w.TracksHint.Text);
                Check.Equal("Space play / pause · ←/→ frame step · Home / End", w.HintText.Text);
                Check.Equal("source.mp4", w.Title);
            }
            using (var win = new TrimShareSplitTests.Win(rig.App.Library.Add(noVoice, 3, new FileInfo(noVoice).Length, null, Array.Empty<string>())))
            {
                var w = win.Window;
                Call(w, "BuildTrackRows", partial);
                Check.Equal("Ticked tracks are saved at their volume · Listen picks what you hear · Game and Mic also shape All audio", w.TracksHint.Text);
                Check.Equal("Voice is only inside All audio (not saved as a track of its own), so its volume cannot be changed. Turning Game or Mic down takes it out of the baked mix again, which can leave a faint trace of it.", w.MixNote.Text);
                SetField(w, "_unlined", new[] { TrackRole.Game });
                Call(w, "UpdateMixNote");
                Check.Equal("Recorded or trimmed by an older Clink: sources cannot be changed inside All audio. The Game volume only changes its own track. Voice is only inside All audio (not saved as a track of its own), so its volume cannot be changed. Turning Mic down takes it out of the baked mix again, which can leave a faint trace of it.", w.MixNote.Text);
                SetField(w, "_unlined", new[] { TrackRole.Game, TrackRole.Mic });
                SetField(w, "_untrusted", true);
                Call(w, "UpdateMixNote");
                Check.True(w.MixNote.Text.StartsWith("This Windows reads clips differently: sources cannot be changed inside All audio. The Game and Mic volumes only change their own tracks.", StringComparison.Ordinal), w.MixNote.Text);
                Call(w, "SetTrimMode", false);
                Check.Equal("Listen picks what you hear · the Game and Mic sliders change All audio, live", w.TracksHint.Text);
            }
            // the two notes are the engine's own words too: the copies in the catalog must not drift from them
            Check.Equal(ClipAlignment.UntrustedNote, L.T("This Windows reads clips differently: sources cannot be changed inside All audio"));
            Check.Equal(ClipAlignment.OlderClinkNote, L.T("Recorded or trimmed by an older Clink: sources cannot be changed inside All audio"));
            // over the account's limits: whole sentences per plan, the limits put in (the English is the old lower-cased refusal, word for word)
            string plusRefusal = AppController.ShareRefusalFor(long.MaxValue, 0, true)!, freeRefusal = AppController.ShareRefusalFor(long.MaxValue, 0, false)!;
            Check.Equal("Saved a (trim).mp4, not uploaded: " + char.ToLowerInvariant(plusRefusal[0]) + plusRefusal[1..] + " - tighten the selection",
                TrimWindow.NotUploadedText("a (trim).mp4", ShareRefusalKind.OverLimit, plusRefusal, true));
            Check.Equal("Saved a (trim).mp4, not uploaded: " + char.ToLowerInvariant(freeRefusal[0]) + freeRefusal[1..] + " - tighten the selection, or tick 'I have Copium+' if you do",
                TrimWindow.NotUploadedText("a (trim).mp4", ShareRefusalKind.OverLimit, freeRefusal, false));
            // a smaller copy that could not be made: its refusal is a whole sentence (with its full stop) and goes in as it is (no case change: other languages need their capitals)
            string failed = ShareFitModel.FailedNote(ShareFitModel.ForCopium(true), "the disk is full", 46_000_000).Shown;
            Check.Equal("Couldn't make a smaller copy: the disk is full. The original is 46 MB, over Copium Plus (2 GB, 60 min).", failed);
            Check.Equal("Saved a.mp4, not uploaded. " + failed + " Tighten the selection to share it.", TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.SmallerCopy, failed, true));
            Check.Equal("Saved a.mp4, not uploaded. " + failed + " Tighten the selection, or tick 'I have Copium+' if you do.", TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.SmallerCopy, failed, false));
            // a refusal that is only a phrase (a game is in front) ends the status
            string game = ShareFitModel.GameRefusalText(ShareFitModel.ForCopium(true));
            Check.Equal("Saved a.mp4, not uploaded. " + game, TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.Phrase, game, true));
            using (L.Scope("de")) Check.True(TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.SmallerCopy, "Zu gross", true).Contains("Zu gross", StringComparison.Ordinal), "a refusal keeps its capital letter");
            using (L.Scope("tr"))
            {
                Check.Equal("İstanbul", ArchiveService.Capitalize("istanbul"), "a leading i is a dotted capital under Turkish");
                Check.True(TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.SmallerCopy, "İstanbul", true).Contains("İstanbul", StringComparison.Ordinal), "the Turkish refusal keeps its capital letter");
            }
            using (L.Scope("qps-ploc"))
            {
                L.ClearMissing();
                Check.True(PseudoLocale.IsPseudo(TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.OverLimit, freeRefusal, false)), "the over-the-limit sentences are translated whole");
                foreach (bool plus in new[] { true, false })
                {
                    string pseudo = TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.SmallerCopy, failed, plus);
                    Check.True(PseudoLocale.IsPseudo(pseudo), "the smaller-copy sentence is translated whole: " + pseudo);
                    Check.False(pseudo.Contains("Tighten", StringComparison.Ordinal) || pseudo.Contains("Saved", StringComparison.Ordinal), "no English left around the refusal: " + pseudo);
                }
                Check.True(PseudoLocale.IsPseudo(TrimWindow.NotUploadedText("a.mp4", ShareRefusalKind.Phrase, game, true)), "the phrase sentence is translated whole");
                Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "both are in trim.json");
            }
        }
        finally { MixRenderTests.OnMta(() => { full.Dispose(); partial.Dispose(); }); }
    }

    private static void Wait(Task task)
    {
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the save", 30_000);
        task.GetAwaiter().GetResult();
    }

    [UnitTest]
    public static void The_save_and_share_status_lines_are_localized_in_every_outcome()
    {
        RequireApp();
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            bool pseudo = locale == "qps-ploc";
            using var rig = new TrimShareSplitTests.Rig(true, true);
            using var scope = L.Scope(locale);
            L.ClearMissing();
            using var win = new TrimShareSplitTests.Win(rig.Entry);
            var w = win.Window;
            void Status(string english, string what)
            {
                if (pseudo) Marked(w.StatusText.Text, AllowTrim, what);
                else Check.Equal(english, w.StatusText.Text, what);
            }
            string target = Path.Combine(rig.Dir.File("elsewhere"), "copy.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // a selection that is too short: the failure says why
            w.Timeline.SelectionStart = 2;
            w.Timeline.SelectionEnd = 2.02;
            Wait(w.SaveAsync(share: false));
            Status("Trim failed: Selection is too short", "too short");
            Check.Equal(0, rig.TrimsOnDisk(), "no file after a failed save");

            w.Timeline.SelectionStart = 1;
            w.Timeline.SelectionEnd = 4;
            // Share file: the share window cannot open
            rig.App.ShareWindowOpener = _ => false;
            Wait(w.SaveAsync(share: true, ShareChoice.ShareFile));
            Status("Saved source (trim).mp4, not shared: could not open the share window. Choose again to share it.", "share window failed");
            // Save a copy: cancelled, then saved
            rig.App.SaveTargetPicker = _ => null;
            Wait(w.SaveAsync(share: true, ShareChoice.SaveCopy));
            Status("Saved source (trim).mp4, no copy saved. Choose again to share it.", "copy cancelled");
            rig.App.SaveTargetPicker = _ => target;
            Wait(w.SaveAsync(share: true, ShareChoice.SaveCopy));
            Status("Saved source (trim).mp4; copy saved to elsewhere", "copy saved");
            Check.True(win.Closed, "a completed choice closes the window");
        }
        NoMissing("save and share status lines");
    }

    [UnitTest]
    public static void The_upload_status_lines_are_localized()
    {
        RequireApp();
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            bool pseudo = locale == "qps-ploc";
            using var rig = new TrimShareSplitTests.Rig(true, true);
            using var scope = L.Scope(locale);
            L.ClearMissing();
            using var win = new TrimShareSplitTests.Win(rig.Entry);
            var w = win.Window;
            void Status(string english, string what)
            {
                if (pseudo) Marked(w.StatusText.Text, AllowTrim, what);
                else Check.Equal(english, w.StatusText.Text, what);
            }
            w.Timeline.SelectionStart = 1;
            w.Timeline.SelectionEnd = 4;
            // declined at the consent prompt: saved, not shared
            rig.App.ConsentPromptOverride = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow));
            Wait(w.SaveAsync(share: true));
            Status("Saved source (trim).mp4, not shared. Choose again to share it.", "declined");
            // cloud sharing off: the same, with the reason
            rig.App.Settings.CloudSharingEnabled = false;
            Wait(w.SaveAsync(share: true));
            Status("Saved source (trim).mp4, not shared: cloud sharing is off", "cloud off");
            rig.App.Settings.CloudSharingEnabled = true;
            // an upload that fails: the reason, and a retry
            var handler = new Offline();
            CopiumClient.HttpOverride = new System.Net.Http.HttpClient(handler);
            try
            {
                rig.App.ConsentPromptOverride = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.Enable));
                Wait(w.SaveAsync(share: true));
                if (pseudo) { Marked(w.StatusText.Text, AllowTrim, "upload failed"); }
                else Check.True(w.StatusText.Text.StartsWith("Saved source (trim).mp4, not uploaded: ", StringComparison.Ordinal) && w.StatusText.Text.EndsWith(". Choose again to retry.", StringComparison.Ordinal), w.StatusText.Text);
            }
            finally { CopiumClient.HttpOverride = null; }
        }
        NoMissing("upload status lines");
    }

    private sealed class Offline : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new System.Net.Http.HttpRequestException("no route");
    }

    // ------------------------------------------------------------------ the archive window

    private static void SetFieldTrue(object target, string name) => SetField(target, name, true);

    [UnitTest]
    public static void The_archive_window_in_every_state_is_localized()
    {
        RequireApp();
        using var rig = new TrimShareSplitTests.Rig(false, true);
        using var archive = new TempDir();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var s = rig.App.Settings;

        // no archive location
        s.ArchivePath = null;
        var none = new ArchiveWindow(rig.App);
        try
        {
            NoneUnlocalized(none, AllowArchive, "no location (before the first answer)");
            Marked(none.DestinationText.Text, "no-location line");
            Wait((Task)Call(none, "ShowDestinationAsync")!);
            Marked(none.DestinationInfo.Text, "no-location answer");
            NoneUnlocalized(none, AllowArchive, "no location");
        }
        finally { none.Close(); }

        // a location, with a rule
        s_archivePaths.Add(archive.Path);
        s.ArchivePath = archive.Path;
        s.ArchiveAfterDays = 3;
        s.ArchiveWhenLocalExceedsGB = 50;
        var w = new ArchiveWindow(rig.App);
        try
        {
            NoneUnlocalized(w, AllowArchive, "testing (the first state)");
            Marked(w.DueRule.Text, "the rules line");
            // reachable and writable
            var ok = rig.App.Archive.TestAsync(archive.Path).GetAwaiter().GetResult();
            Check.True(ok.ok, "the temp folder is a good archive: " + ok.message);
            Call(w, "ShowDestination", ok.message, true);
            Marked(w.DestinationInfo.Text, "ok line");
            NoneUnlocalized(w, AllowArchive, "ok");
            // refused
            var bad = rig.App.Archive.TestAsync("nas\\clips").GetAwaiter().GetResult();
            Check.False(bad.ok, "a relative path is refused");
            Call(w, "ShowDestination", bad.message, false);
            Marked(w.DestinationInfo.Text, "problem line");
            NoneUnlocalized(w, AllowArchive, "problem");
            Call(w, "ShowDestination", ok.message, true);
            // the counts
            Wait((Task)Call(w, "RefreshCountsAsync", true)!);
            Marked(w.DueTitle.Text, "due title");
            Marked(w.AllTitle.Text, "all title");
            NoneUnlocalized(w, AllowArchive, "counted");
            // a run in progress, then cancelling
            SetFieldTrue(w, "_running");
            Call(w, "OnProgress", new ArchiveProgress(2, 7, "clip.mp4", 40));
            Marked(w.StatusText.Text, "progress line");
            Call(w, "UpdateControls");
            NoneUnlocalized(w, AllowArchive, "running");
            Call(w, "Cancel_Click", null, null);
            Marked(w.StatusText.Text, "cancelling line");
            NoneUnlocalized(w, AllowArchive, "cancelling");
            SetField(w, "_running", false);
            SetField(w, "_cancelling", false);
            // what a run says at the end
            foreach (var r in new[]
            {
                new ArchiveRunResult(13, 12, 1, 0, 0, 3L << 30, null, false),
                new ArchiveRunResult(2, 2, 0, 1, 0, 1L << 20, null, true),
                new ArchiveRunResult(5, 2, 0, 0, 3, 5L << 20, L.T("archive is full ({free} free)", ("free", ArchiveService.FormatBytes(1L << 20))), false),
                new ArchiveRunResult(0, 0, 0, 0, 0, 0, null, false),
                new ArchiveRunResult(0, 0, 0, 0, 0, 0, L.T("archive unreachable"), false),
            })
            {
                Call(w, "ShowResult", r);
                Marked(w.StatusText.Text, AllowArchive, "result " + r);
                NoneUnlocalized(w, AllowArchive, "result " + r);
            }
        }
        finally { w.Close(); }
        NoMissing("archive window");
    }

    [UnitTest]
    public static void The_archive_dialog_words_are_what_they_were_in_English_and_marked_under_qps()
    {
        using var inv = Invariant();
        long gb34 = (long)(3.4 * (1L << 30));
        using (L.Scope("en"))
        {
            Check.Equal("0 clips", ArchiveWindow.Clips(0));
            Check.Equal("1 clip", ArchiveWindow.Clips(1));
            Check.Equal("21 clips", ArchiveWindow.Clips(21));
            Check.Equal("older than 1 day", ArchiveWindow.RuleText(1, 0));
            Check.Equal("older than 2 days / while the folder is over 1.5 GB", ArchiveWindow.RuleText(2, 1.5));
            Check.Equal(@"Moved 1 clip (3.4 GB) to T:\clips", ArchiveWindow.ResultText(new ArchiveRunResult(1, 1, 0, 0, 0, gb34, null, false), @"T:\clips"));
            Check.Equal("Nothing was moved · 1 in use, try again later · 2 saved less than 30 s ago, try again later · 3 failed", ArchiveWindow.ResultText(new ArchiveRunResult(6, 0, 1, 2, 3, 0, null, false), @"T:\clips"));
            Check.Equal("Nothing was moved · 1 failed: access denied", ArchiveWindow.ResultText(new ArchiveRunResult(1, 0, 0, 0, 1, 0, "access denied", false), @"T:\clips"));
            Check.Equal("Moving 3 of 12 · Copying clip.mp4 (40 %)", ArchiveWindow.ProgressText(new ArchiveProgress(3, 12, "clip.mp4", 40)));
            Check.Equal("512 KB", ArchiveService.FormatBytes(512 * 1024));
            Check.Equal("120 MB", ArchiveService.FormatBytes(120L << 20));
            Check.Equal("3.4 GB", ArchiveService.FormatBytes(gb34));
            Check.Equal("2.0 TB", ArchiveService.FormatBytes(2L << 40));
            Check.Equal(@"Use a full path such as D:\Clips or \\nas\clips", ArchiveService.FullPathHint);
        }
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            Marked(ArchiveWindow.Clips(3), "Clips");
            Marked(ArchiveWindow.CountText(0, 0), "CountText 0");
            Marked(ArchiveWindow.CountText(5, gb34), "CountText");
            foreach (var (days, gb) in new[] { (1, 0.0), (7, 0.0), (0, 2.5), (3, 10.0), (0, 0.0) }) Marked(ArchiveWindow.RuleText(days, gb), $"RuleText {days}/{gb}");
            Marked(ArchiveWindow.ProgressText(new ArchiveProgress(1, 2, "a.mp4", 5)), "ProgressText");
            foreach (var r in new[]
            {
                new ArchiveRunResult(13, 12, 1, 2, 3, gb34, "archive is full", false),
                new ArchiveRunResult(1, 1, 0, 0, 0, gb34, null, true),
                new ArchiveRunResult(0, 0, 0, 0, 0, 0, null, true),
                new ArchiveRunResult(0, 0, 0, 0, 0, 0, null, false),
                new ArchiveRunResult(2, 0, 0, 0, 0, 0, null, false),
                new ArchiveRunResult(1, 0, 0, 0, 1, 0, null, false),
            })
                Marked(ArchiveWindow.ResultText(r, @"T:\clips"), "ResultText " + r);
            foreach (long b in new[] { 100L, 512 * 1024, 120L << 20, gb34, 3L << 40 }) Marked(ArchiveService.FormatBytes(b), "FormatBytes " + b);
            Marked(ArchiveService.FullPathHint, "FullPathHint");
            NoMissing("archive dialog words");
        }
    }

    // ------------------------------------------------------------------ the archive service

    /// <summary>A clips folder, an archive folder and a service over them (the settings an archive needs and nothing more).</summary>
    private sealed class ServiceRig : IDisposable
    {
        private readonly TempDir _meta = new();
        private readonly string? _index = ClipLibrary.IndexPathOverride;
        public readonly TempDir Clips = new(), Archive = new();
        public readonly Settings Settings;
        public readonly ClipLibrary Library = new();
        public readonly ArchiveService Service;

        public ServiceRig(bool enabled = false)
        {
            ClipLibrary.IndexPathOverride = Path.Combine(_meta.Path, "clips.json");
            Settings = new Settings { ClipsFolder = Clips.Path, ArchivePath = Archive.Path, ArchiveEnabled = enabled };
            Service = new ArchiveService(Settings, Library);
        }

        public void Dispose()
        {
            Service.Stop();
            ClipLibrary.IndexPathOverride = _index;
            Clips.Dispose(); Archive.Dispose(); _meta.Dispose();
        }
    }

    /// <summary>A drive letter nothing is mounted on: a path there is unreachable, answered at once.</summary>
    private static string? MissingDrive()
    {
        var present = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'J'; c--)
            if (!present.Contains(c)) return c + @":\clink-l10n-test";
        return null;
    }

    private static void Statuses(ServiceRig rig, string locale, Action<string, string, bool> check)
    {
        var s = rig.Settings;
        var svc = rig.Service;
        s.ArchivePath = null; s.ArchiveEnabled = false; svc.Reevaluate();
        check("nothing set", "Archive off", false);
        s.ArchivePath = rig.Archive.Path; s.ArchiveEnabled = false; svc.Reevaluate();
        check("a location, switch off", "Automatic moves off · Archive now… moves clips by hand", false);
        s.ArchivePath = null; s.ArchiveEnabled = true; svc.Reevaluate();
        check("switch on, no location", "Automatic moves are on, but no archive location is set", true);
        s.ArchivePath = rig.Archive.Path; s.ArchiveAfterDays = 0; s.ArchiveWhenLocalExceedsGB = 0; svc.Reevaluate();
        check("switch on, no rule", "Automatic moves are on, but no rule is set (days / GB)", true);
        s.ArchiveAfterDays = 2;
        s.ArchivePath = "nas\\clips"; svc.Reevaluate();
        check("not a full path", @"0 clips archived · archive location is not a full path (D:\Clips, \\nas\clips)", true);
        s.ArchivePath = rig.Clips.Path; svc.Reevaluate();
        check("the clips folder", "0 clips archived · archive location is the clips folder", true);
        s.ArchivePath = rig.Archive.Path; svc.Reevaluate();
        check("a good location", "0 clips archived", false);
    }

    [UnitTest]
    public static void The_status_line_says_whether_it_is_a_problem_in_every_language_and_stays_English_for_the_log()
    {
        using var inv = Invariant();
        using (L.Scope("en"))
        {
            using var rig = new ServiceRig(true);
            Statuses(rig, "en", (what, english, problem) =>
            {
                Check.Equal(english, rig.Service.Status, what);
                Check.Equal(problem, rig.Service.StatusIsProblem, what + ": the flag");
                var (text, flag) = rig.Service.ReadStatus();
                Check.Equal((english, problem), (text, flag), what + ": read together");
            });
        }
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            using var rig = new ServiceRig(true);
            var seen = new List<string>();
            Statuses(rig, "qps-ploc", (what, english, problem) =>
            {
                Marked(rig.Service.Status, what);
                Check.Equal(problem, rig.Service.StatusIsProblem, what + ": the flag does not depend on the words");
                seen.Add(rig.Service.Status);
            });
            Check.Equal(7, seen.Distinct().Count(), "seven different lines");
            NoMissing("status lines");
        }
    }

    [UnitTest]
    public static void The_card_colours_the_line_by_the_flag_and_not_by_its_words()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // not run from a checkout: nothing to read
        string code = File.ReadAllText(Path.Combine(root, "MainWindow.xaml.cs"));
        int start = code.IndexOf("private void RefreshArchiveStatus()", StringComparison.Ordinal);
        Check.True(start > 0, "RefreshArchiveStatus is there");
        int end = code.IndexOf("// ------------------------------------------------------------------ window chrome", start, StringComparison.Ordinal);
        string method = code[start..end];
        Check.Contains(method, "ReadStatus()");
        Check.Contains(method, "problem ? \"Warn\" : \"Muted\"");
        foreach (var word in new[] { "\"will retry\"", "\"unreachable\"", "\"denied\"", "\"no rule\"", "\"archive location\"", ".Contains(" })
            Check.DoesNotContain(method, word, "the colour reads no words");
        // and a status that merely mentions one of the words is not a problem
        using var rig = new ServiceRig(true);
        File.WriteAllBytes(rig.Clips.File("denied unreachable will retry.mp4"), new byte[300 * 1024]);
        File.SetLastWriteTimeUtc(rig.Clips.File("denied unreachable will retry.mp4"), DateTime.UtcNow.AddMinutes(-60));
        rig.Library.Load(rig.Clips.Path);
        var seen = new List<(string Text, bool Problem)>();
        var sink = new Sink { OnReport = _ => seen.Add(rig.Service.ReadStatus()) };
        var result = rig.Service.RunManualAsync(ArchiveScope.All, false, sink, CancellationToken.None).GetAwaiter().GetResult();
        Check.Equal(1, result.Moved, "the clip moved");
        var copying = seen.Where(t => t.Text.Contains("Copying", StringComparison.Ordinal)).ToList();
        Check.True(copying.Count > 0 && copying.All(t => t.Text.Contains("denied unreachable will retry.mp4", StringComparison.Ordinal) && !t.Problem), "the copying line names the file and is no problem: " + string.Join(" | ", seen.Select(t => t.Text)));
    }

    private sealed class Sink : IProgress<ArchiveProgress>
    {
        public Action<ArchiveProgress>? OnReport;
        public void Report(ArchiveProgress value) => OnReport?.Invoke(value);
    }

    [UnitTest]
    public static void The_copying_lines_and_the_idle_parts_are_localized()
    {
        using var inv = Invariant();
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            using var scope = L.Scope(locale);
            using var rig = new ServiceRig(true);
            rig.Settings.ArchiveAfterDays = 1;
            File.WriteAllBytes(rig.Clips.File("clip.mp4"), new byte[300 * 1024]);
            File.SetLastWriteTimeUtc(rig.Clips.File("clip.mp4"), DateTime.UtcNow.AddMinutes(-60));
            rig.Library.Load(rig.Clips.Path);
            var lines = new List<string>();
            var sink = new Sink { OnReport = _ => lines.Add(rig.Service.Status) };
            rig.Service.RunManualAsync(ArchiveScope.All, false, sink, CancellationToken.None).GetAwaiter().GetResult();
            Check.True(lines.Count > 0, "the run reported");
            if (locale == "en")
            {
                Check.Equal("Copying clip.mp4…", lines[0]);
                Check.True(lines.Any(l => l.StartsWith("Copying clip.mp4 (", StringComparison.Ordinal) && l.EndsWith(" %)", StringComparison.Ordinal)), string.Join(" | ", lines));
                Check.True(rig.Service.Status.StartsWith("1 clip archived · last move ", StringComparison.Ordinal), rig.Service.Status);
            }
            else
            {
                foreach (var line in lines) Marked(line, "copying line");
                Marked(rig.Service.Status, "idle line after a move");
            }
        }
    }

    [UnitTest]
    public static void The_test_button_answers_and_the_refusals_of_a_run_are_localized_and_the_log_stays_English()
    {
        using var inv = Invariant();
        string? missing = MissingDrive();
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            bool pseudo = locale == "qps-ploc";
            using var scope = L.Scope(locale);
            L.ClearMissing();
            using var rig = new ServiceRig(true);
            s_archivePaths.Add(rig.Archive.Path); s_archivePaths.Add(rig.Clips.Path);
            void Answer(string path, bool ok, string english, string what)
            {
                var a = rig.Service.TestAsync(path).GetAwaiter().GetResult();
                Check.Equal(ok, a.ok, what + ": ok");
                if (pseudo) Marked(a.message, AllowArchive, what);
                else if (english.EndsWith("*", StringComparison.Ordinal)) Check.True(Regex.IsMatch(a.message, "^" + Regex.Escape(english.TrimEnd('*')) + @"[\d.]+ [KMGT]B free$"), what + ": " + a.message);
                else Check.Equal(english, a.message, what);
            }
            Answer("", false, "Enter a folder or share path first", "empty");
            Answer("nas\\clips", false, @"Use a full path such as D:\Clips or \\nas\clips", "relative");
            Answer(rig.Clips.Path, false, "Archive location is the clips folder: pick another folder, share or drive", "the clips folder");
            Answer(rig.Archive.Path, true, "Folder reachable · writable · *", "good");
            if (missing != null) Answer(missing, false, "Archive unreachable", "unreachable");

            // refused before it begins
            string? Refusal(string? path)
            {
                rig.Settings.ArchivePath = path;
                return rig.Service.RunManualAsync(ArchiveScope.All, false, null, CancellationToken.None).GetAwaiter().GetResult().Problem;
            }
            void Reason(string? path, string english, string what)
            {
                string? p = Refusal(path);
                if (pseudo) Marked(p, what); else Check.Equal(english, p, what);
            }
            Reason("", "no archive location is set", "no location");
            Reason("nas\\clips", @"archive location is not a full path (D:\Clips, \\nas\clips)", "not a full path");
            Reason(rig.Clips.Path, "archive location is the clips folder", "the clips folder");
            if (missing != null) Reason(missing, "archive unreachable", "unreachable");

            // the guard's words, and the English twin the log takes
            string clip = rig.Clips.File("g.mp4"), copy = rig.Archive.File("g.mp4");
            File.WriteAllBytes(clip, new byte[100]);
            var written = File.GetLastWriteTimeUtc(clip);
            using (var none = ArchiveService.DeleteGuard.OpenCore(clip, rig.Archive.File("nothing.mp4"), 100, written, out var gone, out var goneText))
            {
                Check.Null(none);
                Check.Equal(ArchiveService.GuardResult.CopyMissing, gone);
                Check.Equal("the archived copy is missing", goneText!.English, "the log is English");
                if (pseudo) Marked(goneText.Shown, "copy missing"); else Check.Equal("the archived copy is missing", goneText.Shown);
            }
            File.WriteAllBytes(copy, new byte[60]);
            using (var shortCopy = ArchiveService.DeleteGuard.OpenCore(clip, copy, 100, written, out var differs, out var differsText))
            {
                Check.Null(shortCopy);
                Check.Equal(ArchiveService.GuardResult.CopyDiffers, differs);
                Check.Equal("the archived copy is 60 bytes, the clip 100", differsText!.English, "the log is English");
                if (pseudo) Marked(differsText.Shown, "copy differs"); else Check.Equal("the archived copy is 60 bytes, the clip 100", differsText.Shown);
            }
        }
        NoMissing("test button and refusals");
    }

    [UnitTest]
    public static void A_failed_pass_and_an_unreachable_archive_are_problems_with_localized_words()
    {
        string? missing = MissingDrive();
        if (missing == null) return;   // every letter is mounted: nothing unreachable to point at
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            using var scope = L.Scope(locale);
            L.ClearMissing();
            using var rig = new ServiceRig(true);
            rig.Settings.ArchiveAfterDays = 1;
            rig.Settings.ArchivePath = missing;
            rig.Service.RunAutomaticOnce();
            Check.True(rig.Service.StatusIsProblem, "an unreachable archive is a problem");
            if (locale == "en") Check.Equal("0 clips archived · archive unreachable, will retry", rig.Service.Status);
            else Marked(rig.Service.Status, "unreachable line");
        }
        NoMissing("unreachable");
    }

    // ------------------------------------------------------------------ the third-party notices window

    [UnitTest]
    public static void The_third_party_notices_window_chrome_is_localized_and_the_notices_text_is_not()
    {
        RequireApp();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var w = new ThirdPartyNoticesWindow();
        try
        {
            NoneUnlocalized(w, _ => false, "the notices window");
            Marked(w.Title, "title");
            Marked(System.Windows.Automation.AutomationProperties.GetName(w.NoticesText), "automation name of the box");
            Marked((string)w.CloseButton.Content, "Close button");
            // the licence text itself is the embedded file, as it is
            Check.Equal(ThirdPartyNotices.Load(), w.NoticesText.Text, "the notices are not translated");
            NoMissing("notices window");
        }
        finally { w.Close(); }
        using (L.Scope("en"))
        {
            var e = new ThirdPartyNoticesWindow();
            try
            {
                Check.Equal("Third-party notices", e.Title);
                Check.Equal("Third-party notices", System.Windows.Automation.AutomationProperties.GetName(e.NoticesText));
                Check.Equal("Close", (string)e.CloseButton.Content);
            }
            finally { e.Close(); }
        }
    }

    // ------------------------------------------------------------------ recording policy, self-install, the player's words

    [UnitTest]
    public static void The_recording_policy_words_are_what_they_were_in_English_and_marked_under_qps()
    {
        using var inv = Invariant();
        using (L.Scope("en"))
        {
            Check.Equal("Recording 00:12", RecordingPolicy.StatusLine(12.7, null));
            Check.Equal("Recording 1:02:03 · Neon Drift", RecordingPolicy.StatusLine(3723, "Neon Drift"));
            Check.Equal("12:34 · 1.2 GB", RecordingPolicy.SavedSummary(754, 1_200_000_000));
            Check.Equal("Recovered 1 recording", RecordingPolicy.RecoveredTitle(1));
            Check.Equal("Recovered 3 recordings", RecordingPolicy.RecoveredTitle(3));
            Check.Equal(("Recording stopped", "The disk is nearly full. The recording was saved."), RecordingPolicy.Problem(Result(RecordingEndReason.DiskFull, "a.mp4", null))!.Value);
            Check.Equal(("Recording stopped", "The disk is nearly full."), RecordingPolicy.Problem(Result(RecordingEndReason.DiskFull, null, null))!.Value);
            Check.Equal(("Recording stopped", "Writing the file failed. The recording was recovered and saved."), RecordingPolicy.Problem(Result(RecordingEndReason.WriteFailed, "a.mp4", null))!.Value);
            Check.Equal(("Recording stopped", "Writing the file failed: no space The recording was recovered and saved."), RecordingPolicy.Problem(Result(RecordingEndReason.WriteFailed, "a.mp4", "no space"))!.Value);
            Check.Equal(("Recording stopped", "The disk could not keep up with the recording. The recording was recovered and saved."), RecordingPolicy.Problem(Result(RecordingEndReason.Overflow, "a.mp4", null))!.Value);
            Check.Equal(("Recording stopped", "The recording could not be saved."), RecordingPolicy.Problem(Result(RecordingEndReason.Unrecoverable, null, null))!.Value);
            Check.Equal(("Recording stopped", "The recording could not be saved: boom"), RecordingPolicy.Problem(Result(RecordingEndReason.Unrecoverable, null, "boom"))!.Value);
            Check.Null(RecordingPolicy.Problem(Result(RecordingEndReason.UserStopped, "a.mp4", null)));
        }
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            Marked(RecordingPolicy.StatusLine(12, null), "StatusLine");
            Marked(RecordingPolicy.StatusLine(12, "Neon Drift"), "StatusLine with a game");
            Marked(RecordingPolicy.SavedSummary(754, 1_200_000_000), "SavedSummary");
            foreach (int n in new[] { 1, 3 }) Marked(RecordingPolicy.RecoveredTitle(n), "RecoveredTitle");
            foreach (var reason in new[] { RecordingEndReason.DiskFull, RecordingEndReason.WriteFailed, RecordingEndReason.Overflow, RecordingEndReason.Unrecoverable })
                foreach (string? path in new[] { null, "a.mp4" })
                    foreach (string? error in new[] { null, "boom" })
                    {
                        var p = RecordingPolicy.Problem(Result(reason, path, error))!.Value;
                        Marked(p.Title, "problem title " + reason);
                        Marked(p.Text, _ => false, $"problem text {reason}/{path}/{error}");
                    }
            NoMissing("recording policy");
        }
    }

    private static RecordingResult Result(RecordingEndReason reason, string? path, string? error)
    {
        return new RecordingResult(path, 1, 1, null, Array.Empty<string>(), reason, error, 0);
    }

    private sealed class FakeSystem : ISelfInstallSystem
    {
        public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public bool TargetIsClink = true, TargetReadOnly, CorruptCopy, ChildEnds, StartThrows, DeleteThrows;
        public bool FileExists(string path) => Files.ContainsKey(path);
        public bool IsReadOnly(string path) => TargetReadOnly;
        public void ClearReadOnly(string path) { }
        public void CreateDirectory(string path) { }
        public string Sha256(string path) => Files[path];
        public void Copy(string from, string to) => Files[to] = CorruptCopy ? Files[from] + "!" : Files[from];
        public void Move(string from, string to, bool overwrite)
        {
            if (!overwrite && Files.ContainsKey(to)) throw new IOException("exists");
            Files[to] = Files[from];
            Files.Remove(from);
        }
        public void Delete(string path)
        {
            if (DeleteThrows) throw new IOException("locked");
            Files.Remove(path);
        }
        public bool SameFile(string a, string b) => false;
        public ReleaseVersion? VersionOf(string exePath) => null;
        public bool LooksLikeClink(string exePath) => TargetIsClink;
        public void StripZoneIdentifier(string path) { }
        public IChildProcess Start(string exePath, IReadOnlyList<string> args) =>
            StartThrows ? throw new SelfInstallException(Note.Of("The new copy could not be started.")) : new Child(ChildEnds);
        public void Recycle(string path, nint? ownerHwnd) => Files.Remove(path);
        public uint DriveTypeOf(string root) => 3;
        public IReadOnlyList<string> ProgramFilesFolders() => Array.Empty<string>();
        public void Sleep(int milliseconds) { }

        private sealed class Child(bool ends) : IChildProcess
        {
            public bool WaitForExit(int milliseconds) => ends;
            public void Dispose() { }
        }
    }

    private const string Src = @"C:\Downloads\Clink.exe", Folder = @"C:\Users\u\AppData\Local\Programs\Clink", Target = Folder + @"\Clink.exe";
    private static readonly ReleaseVersion Current = ReleaseVersion.Parse("0.4.2")!.Value;

    [UnitTest]
    public static void The_install_refusals_and_failures_keep_the_log_English_and_give_the_user_localized_text()
    {
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            bool pseudo = locale == "qps-ploc";
            using var scope = L.Scope(locale);
            L.ClearMissing();
            void Shown(string? user, string english, string what)
            {
                if (pseudo) Marked(user, what); else Check.Equal(english, user, what);
            }
            RelocationPlan PlanOf(FakeSystem sys, bool elevated = false, string? src = null, string? dev = null) => SelfInstall.Plan(src ?? Src, Folder, elevated, Current, sys, dev);
            RelocationResult Run(FakeSystem sys)
            {
                sys.Files[Src] = "exe";
                var r = SelfInstall.Execute(PlanOf(sys), 1, null, sys);
                return r;
            }

            // refusals of the plan
            var refused = PlanOf(new FakeSystem(), src: "Clink.exe");
            Check.Equal("The paths are not full paths.", refused.Reason, "the log is English");
            Shown(refused.UserReason, "The paths are not full paths.", "paths");
            var elevated = PlanOf(new FakeSystem(), elevated: true);
            Check.Equal("Clink is running as administrator; it will not install itself from an elevated process.", elevated.Reason);
            Shown(elevated.UserReason, elevated.Reason!, "elevated");
            var outside = PlanOf(new FakeSystem(), dev: @"C:\dev\data");
            Check.Equal("A --dev instance only installs inside its own dev folder.", outside.Reason);
            Shown(outside.UserReason, outside.Reason!, "dev");
            var foreign = new FakeSystem { TargetIsClink = false };
            foreign.Files[Target] = "other";
            var notClink = PlanOf(foreign);
            Check.Equal("The file at the target is not Clink; it is left alone.", notClink.Reason);
            Shown(notClink.UserReason, notClink.Reason!, "foreign exe");
            var r0 = SelfInstall.Execute(refused, 1, null, new FakeSystem());
            Check.Equal(refused.Reason, r0.Error);
            Shown(r0.UserError, refused.Reason!, "refused result");

            // failures of the run
            var gone = SelfInstall.Execute(new RelocationPlan(RelocationKind.Install, Src, Target), 1, null, new FakeSystem());
            Check.Equal("The source file is gone.", gone.Error);
            Shown(gone.UserError, gone.Error!, "source gone");
            var notMine = new FakeSystem { TargetIsClink = false };
            notMine.Files[Src] = "exe"; notMine.Files[Target] = "other";
            var r1 = SelfInstall.Execute(new RelocationPlan(RelocationKind.Install, Src, Target), 1, null, notMine);
            Check.Equal("The file in the target folder is not Clink; it is left alone.", r1.Error);
            Shown(r1.UserError, r1.Error!, "foreign target");
            var ro = new FakeSystem { TargetReadOnly = true };
            ro.Files[Src] = "exe"; ro.Files[Target] = "old";
            var r2 = SelfInstall.Execute(new RelocationPlan(RelocationKind.Install, Src, Target), 1, null, ro);
            Check.Equal("The existing Clink.exe in the target folder is read-only.", r2.Error);
            Shown(r2.UserError, r2.Error!, "read-only target");
            var corrupt = new FakeSystem { CorruptCopy = true };
            var r3 = Run(corrupt);
            Check.Equal("The copy does not match the original (hash mismatch).", r3.Error);
            Shown(r3.UserError, r3.Error!, "hash mismatch");
            var ended = new FakeSystem { ChildEnds = true };
            var r4 = Run(ended);
            Check.Equal("The new copy ended right after it started.", r4.Error);
            Check.Null(r4.RollbackError, "everything was put back");
            Shown(r4.UserError, r4.Error!, "child ended");
            var throws = new FakeSystem { StartThrows = true };
            var r5 = Run(throws);
            Check.Equal("The new copy could not be started.", r5.Error, "the exception message is English");
            Shown(r5.UserError, r5.Error!, "start threw");
            var stuck = new FakeSystem { ChildEnds = true, DeleteThrows = true };
            var r6 = Run(stuck);
            Check.Equal(RelocationOutcome.Failed, r6.Outcome);
            Check.True(r6.Error!.StartsWith("The new copy ended right after it started. (Rollback incomplete: could not remove ", StringComparison.Ordinal), "English with the rollback: " + r6.Error);
            Check.Equal("could not remove " + Target + ": locked", r6.RollbackError, "the rollback error is English");
            Shown(r6.UserError, r6.Error!, "rollback incomplete");
            // a good install says nothing
            var fine = new FakeSystem();
            var ok = Run(fine);
            Check.Equal(RelocationOutcome.Moved, ok.Outcome);
            Check.Null(ok.UserError);
        }
        NoMissing("self-install");
    }

    [UnitTest]
    public static void The_clip_players_audio_problems_are_localized_and_the_log_text_stays_English()
    {
        using (L.Scope("en"))
        {
            Check.Equal("no output device, the preview is silent", ClipPlayer.AudioIssueText(PlayerAudioIssue.NoDevice));
            Check.Equal("the output device stopped responding, the preview is silent", ClipPlayer.AudioIssueText(PlayerAudioIssue.Stopped));
            Check.Equal("the output device could not start, the preview is silent", ClipPlayer.AudioIssueText(PlayerAudioIssue.CouldNotStart));
        }
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            foreach (var issue in Enum.GetValues<PlayerAudioIssue>()) Marked(ClipPlayer.AudioIssueText(issue), "audio issue " + issue);
            var ex = new ClipPlayerException(Note.Of("No video stream in file"));
            Check.Equal("No video stream in file", ex.Message, "the log text is English");
            Marked(UserText.Of(ex), "exception text");
            Marked(L.T("Decoder reported an empty frame size"), "decoder");
            Marked(L.T("Audio stream cannot be decoded to PCM"), "pcm");
            Marked(L.T("Unsupported PCM sample size"), "sample size");
            NoMissing("player");
        }
    }

    // ------------------------------------------------------------------ plural sites under synthetic catalogs

    private static IDisposable Synthetic(string locale, Dictionary<string, Dictionary<string, string>> forms)
    {
        var en = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var kv in L.Embedded.English.Entries) en[kv.Key] = kv.Value.Text != null ? kv.Value.Text : kv.Value.Forms!;
        var other = forms.ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.Ordinal);
        return L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "all", JsonSerializer.Serialize(en)),
            new CatalogSource(locale, "t", JsonSerializer.Serialize(other)),
        }));
    }

    private static Dictionary<string, string> Cats(string tag, params string[] cats) =>
        (cats.Length == 0 ? new[] { "one", "few", "many", "other" } : cats).ToDictionary(c => c, c => $"{tag}-{c} {{count}}");

    private static readonly (int N, string Ru, string Pl)[] s_counts = { (1, "one", "one"), (2, "few", "few"), (5, "many", "many"), (21, "one", "many") };

    private static readonly string[] s_pluralKeys =
    {
        "{count} clips", "older than {count} days", "Moved {count} clips ({size}) to {destination}", "Cancelled after moving {count} clips ({size}) to {destination}",
        "{count} in use, try again later", "{count} saved less than {age} ago, try again later", "{count} failed: {problem}", "{count} failed",
        "{count} clips archived", "Recovered {count} recordings",
    };

    private static string Id(string key) => "k" + Array.IndexOf(s_pluralKeys, key);

    [UnitTest]
    public static void The_plural_sites_pick_the_right_form_in_ru_and_pl()
    {
        foreach (var locale in new[] { "ru", "pl" })
        {
            using var catalog = Synthetic(locale, s_pluralKeys.ToDictionary(k => k, k => Cats(Id(k))));
            using var scope = L.Scope(locale);
            foreach (var (n, ru, pl) in s_counts)
            {
                string cat = locale == "ru" ? ru : pl;
                string Want(string key) => $"{Id(key)}-{cat} {n}";
                string at = $" ({locale}, {n})";
                Check.Equal(Want("{count} clips"), ArchiveWindow.Clips(n), "Clips" + at);
                Check.Equal(Want("{count} clips"), ArchiveWindow.CountText(n, 0).Split(" · ")[0], "CountText" + at);
                Check.Equal(Want("older than {count} days"), ArchiveWindow.RuleText(n, 0), "RuleText" + at);
                Check.Equal(Want("Moved {count} clips ({size}) to {destination}"), ArchiveWindow.ResultText(new ArchiveRunResult(n, n, 0, 0, 0, 1000, null, false), "T:"), "Moved" + at);
                Check.Equal(Want("Cancelled after moving {count} clips ({size}) to {destination}"), ArchiveWindow.ResultText(new ArchiveRunResult(n, n, 0, 0, 0, 1000, null, true), "T:"), "Cancelled after moving" + at);
                Check.True(ArchiveWindow.ResultText(new ArchiveRunResult(n + 1, 0, n, 0, 0, 0, null, false), "T:").EndsWith(" · " + Want("{count} in use, try again later"), StringComparison.Ordinal), "in use" + at);
                Check.True(ArchiveWindow.ResultText(new ArchiveRunResult(n + 1, 0, 0, n, 0, 0, null, false), "T:").EndsWith(" · " + Want("{count} saved less than {age} ago, try again later"), StringComparison.Ordinal), "too new" + at);
                Check.True(ArchiveWindow.ResultText(new ArchiveRunResult(n + 1, 0, 0, 0, n, 0, null, false), "T:").EndsWith(" · " + Want("{count} failed"), StringComparison.Ordinal), "failed" + at);
                Check.True(ArchiveWindow.ResultText(new ArchiveRunResult(n + 1, 0, 0, 0, n, 0, "why", false), "T:").EndsWith(" · " + Want("{count} failed: {problem}"), StringComparison.Ordinal), "failed with a reason" + at);
                Check.Equal(Want("Recovered {count} recordings"), RecordingPolicy.RecoveredTitle(n), "RecoveredTitle" + at);
            }
        }
    }

    [UnitTest]
    public static void The_archived_count_of_the_status_line_picks_the_right_form_in_ru()
    {
        using var catalog = Synthetic("ru", s_pluralKeys.ToDictionary(k => k, k => Cats(Id(k))));
        using var scope = L.Scope("ru");
        using var rig = new ServiceRig(true);
        rig.Settings.ArchiveAfterDays = 1;
        int archived = 0;
        foreach (var (n, ru, _) in s_counts)
        {
            while (archived < n)
            {
                var e = rig.Library.Add(rig.Clips.File($"a{archived}.mp4"), 1, 1000, null, Array.Empty<string>());
                e.ArchivedAt = DateTime.Now;
                archived++;
            }
            rig.Service.Reevaluate();
            Check.Equal($"{Id("{count} clips archived")}-{ru} {n}", rig.Service.Status, $"{n} archived");
        }
    }

    [UnitTest]
    public static void The_plural_sites_use_all_six_forms_in_a_synthetic_Arabic_catalog()
    {
        string[] cats = { "zero", "one", "two", "few", "many", "other" };
        using var catalog = Synthetic("ar", s_pluralKeys.ToDictionary(k => k, k => Cats(Id(k), cats)));
        using var scope = L.Scope("ar");
        foreach (var (n, cat) in new[] { (0, "zero"), (1, "one"), (2, "two"), (3, "few"), (11, "many"), (100, "other") })
        {
            string at = " (ar, " + n + ")";
            Check.Equal($"{Id("{count} clips")}-{cat} {n}", ArchiveWindow.Clips(n), "Clips" + at);
            if (n > 0) Check.Equal($"{Id("older than {count} days")}-{cat} {n}", ArchiveWindow.RuleText(n, 0), "RuleText" + at);
            Check.Equal($"{Id("Recovered {count} recordings")}-{cat} {n}", RecordingPolicy.RecoveredTitle(n), "RecoveredTitle" + at);
            if (n > 0) Check.Equal($"{Id("Moved {count} clips ({size}) to {destination}")}-{cat} {n}", ArchiveWindow.ResultText(new ArchiveRunResult(n + 1, n, 0, 0, 0, 1000, null, false), "T:").Split(" · ")[0], "Moved" + at);
        }
    }

    // ------------------------------------------------------------------ every key the slice's sources ask for is in a catalog, and every trim.json key is used

    [UnitTest]
    public static void Every_key_in_the_slice_sources_is_in_a_catalog_and_every_trim_key_is_used()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // not run from a checkout: nothing to read
        var trim = L10nSourceScan.AreaKeys(root, "trim");
        var everywhere = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Locales", "en"), "*.json"))
            everywhere.UnionWith(L10nSourceScan.AreaKeys(root, Path.GetFileNameWithoutExtension(file)));
        foreach (string area in new[] { "core", "main", "clips", "editor" })
            if (area != "trim") foreach (string k in trim) if (L10nSourceScan.AreaKeys(root, area).Contains(k)) Check.Fail($"\"{k}\" is in trim.json and in {area}.json: a key lives in one file");

        var files = new[]
        {
            @"UI\TrimWindow.xaml.cs", @"UI\ArchiveWindow.xaml.cs", @"UI\ThirdPartyNoticesWindow.xaml.cs", @"UI\TimelineControl.cs",
            @"Services\ArchiveService.cs", @"Services\SelfInstall.cs", @"Services\InstallLocation.cs", @"Services\RecordingPolicy.cs", @"Playback\ClipPlayer.cs",
        };
        // L.T / L.Plural / Note.Of (exception texts) / ArchiveProblem.Of / unit( with a key or a "cond ? key : key" first argument, and L.TIn("en", key)
        var call = new Regex(@"(?<![\w])(?:L\.T|L\.Plural|Note\.Of|ArchiveProblem\.Of|unit)\(\s*(?:[^""\r\n]*?\?\s*)?""((?:[^""\\]|\\.)*)""(?:\s*:\s*""((?:[^""\\]|\\.)*)"")?"
            + @"|(?<![\w])L\.TIn\(""en"",\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
        var hits = new List<ScanHit>();
        foreach (string file in files)
            foreach (Match m in call.Matches(File.ReadAllText(Path.Combine(root, file))))
                foreach (int g in new[] { 1, 2, 3 })
                    if (m.Groups[g].Success) hits.Add(new ScanHit(file, L10nSourceScan.UnescapeCSharp(m.Groups[g].Value)));
        hits.AddRange(L10nSourceScan.Xaml(root, new[] { @"UI\TrimWindow.xaml", @"UI\ArchiveWindow.xaml", @"UI\ThirdPartyNoticesWindow.xaml" }));
        var problems = hits.Where(h => !everywhere.Contains(h.Key)).Select(h => h.File + ": " + h.Key).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check.True(hits.Count > 200, "found the literals of the slice's sources: " + hits.Count);
        Check.Equal(Array.Empty<string>(), problems, "keys the sources ask for that no catalog has");
        var used = hits.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var unused = trim.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Check.Equal(Array.Empty<string>(), unused, "trim.json keys no source of the slice uses");
    }
}
