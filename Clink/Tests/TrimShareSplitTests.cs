using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// The trim editor's split "Save &amp; share" (token and cloud sharing on): the gate, that each menu choice runs only on a saved trim,
/// and the layout at the window's minimum width (laid out offscreen, never shown). Pictures: see <see cref="ToastShareLayoutTests"/>.
/// </summary>
internal static class TrimShareSplitTests
{
    private const double MinWidth = 720, MinHeight = 560, RequiredSlack = 8;

    // ------------------------------------------------------------------ the gate

    [UnitTest]
    public static void Save_and_share_is_split_only_with_a_token_and_cloud_sharing_on()
    {
        Check.True(ShareChoices.TrimSplit(true, true), "token + cloud on: split");
        Check.False(ShareChoices.TrimSplit(false, true), "no token: plain button (it opens the share window)");
        Check.False(ShareChoices.TrimSplit(true, false), "cloud off: no split (the button is hidden)");
        Check.False(ShareChoices.TrimSplit(false, false), "neither");
        // the same gate as the rows
        foreach (bool token in new[] { false, true })
            foreach (bool cloud in new[] { false, true })
                Check.Equal(ShareChoices.IsSplit(token, cloud, false), ShareChoices.TrimSplit(token, cloud), $"token {token}, cloud {cloud}");
        Check.Null(ShareChoices.ForTrim(false, true));
        Check.Null(ShareChoices.ForTrim(true, false));
        var items = ShareChoices.ForTrim(true, true)!;
        // a saved trim is a new clip with no link: never "Copy link"
        Check.Equal(new[] { ShareChoice.Upload, ShareChoice.ShareFile, ShareChoice.SaveCopy }, items.Select(i => i.Choice).ToArray());
        Check.True(items.All(i => i.Header.StartsWith("Save & ")), "every label says the trim is saved first");
    }

    [UnitTest]
    public static void The_buttons_follow_the_gate_in_each_state()
    {
        foreach (var (token, cloud) in new[] { (false, false), (false, true), (true, false), (true, true) })
        {
            using var rig = new Rig(token, cloud);
            using var win = new Win(rig.Entry);
            var window = win.Window;
            window.UpdateShareButtons();
            bool split = token && cloud;
            Check.Equal(split, window.SaveShareSplit.Visibility == Visibility.Visible, $"split visible (token {token}, cloud {cloud})");
            Check.Equal(cloud && !split, window.SaveShareButton.Visibility == Visibility.Visible, $"plain visible (token {token}, cloud {cloud})");
            Check.Equal("Save & share", (string)window.SaveShareMain.Content);
            Check.Equal("Save & share", (string)window.SaveShareButton.Content);
        }
    }

    [UnitTest]
    public static void The_main_half_shares_the_file_like_the_Library_and_the_menu_holds_the_upload_and_the_rest()
    {
        Check.Equal(ShareChoice.ShareFile, ShareChoices.SplitMainChoice, "the Library's main action");
        var library = ShareChoices.For(true, true, false)!.Select(i => i.Choice).ToArray();
        var trim = ShareChoices.ForTrim(true, true)!.Select(i => i.Choice).ToArray();
        Check.Equal(library, trim, "the trim menu offers the Library menu's choices (Upload to Copium, Share file, Save a copy)");
        using var rig = new Rig(true, true);
        using var win = new Win(rig.Entry);
        var window = win.Window;
        window.Timeline.SelectionStart = 1;
        window.Timeline.SelectionEnd = 4;
        var opened = new List<string>();
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            rig.App.ShareWindowOpener = e => { opened.Add(e.Path); return true; };
            window.SaveShareMain.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ScreenshotUploadTests.Pump(() => win.Closed, "the main click", 30_000);
            Check.Equal(1, opened.Count, "the main click opened the share window");
            Check.True(opened[0].EndsWith("(trim).mp4"), "on the saved trim");
            Check.Equal(0, handler.Requests, "the main click uploads nothing");
        }
        finally { CopiumClient.HttpOverride = null; }
    }

    // ------------------------------------------------------------------ save, then the choice

    /// <summary>A handler that refuses every request (never a real network): the upload fails with "could not reach Copium".</summary>
    private sealed class Offline : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("no route");
        }
    }

    private static bool Enabled(TrimWindow w) => w.SaveShareMain.IsEnabled && w.SaveShareArrow.IsEnabled && w.SaveButton.IsEnabled;

    [UnitTest]
    public static void Each_menu_choice_runs_on_the_saved_trim_and_only_after_it_is_saved()
    {
        foreach (var choice in new[] { ShareChoice.Upload, ShareChoice.ShareFile, ShareChoice.SaveCopy })
        {
            using var rig = new Rig(true, true);
            using var win = new Win(rig.Entry);
            var window = win.Window;
            var seen = new List<(string Path, bool Existed, bool IsTrim)>();
            string target = Path.Combine(rig.Dir.File("elsewhere"), "copy.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var handler = new Offline();
            CopiumClient.HttpOverride = new HttpClient(handler);
            try
            {
                // the real dispatch, through the controller's own seams
                rig.App.ShareWindowOpener = e => { seen.Add((e.Path, File.Exists(e.Path), e.IsTrim)); return true; };
                rig.App.SaveTargetPicker = e => { seen.Add((e.Path, File.Exists(e.Path), e.IsTrim)); return target; };
                rig.App.ConsentPromptOverride = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow));
                // a selection that is too short: the save fails, nothing runs
                window.Timeline.SelectionStart = 2;
                window.Timeline.SelectionEnd = 2.02;
                Wait(window.SaveAsync(share: true, choice));
                Check.Equal(0, seen.Count, $"{choice}: not run after a failed save");
                Check.Equal(0, handler.Requests, $"{choice}: nothing uploaded after a failed save");
                Check.Equal(0, rig.TrimsOnDisk(), $"{choice}: a failed save leaves no file");
                Check.True(Enabled(window), $"{choice}: halves enabled again after a failure");
                // a good selection: saved first, then the choice, once, on the new clip
                window.Timeline.SelectionStart = 1;
                window.Timeline.SelectionEnd = 4;
                Wait(window.SaveAsync(share: true, choice));
                Check.Equal(1, rig.TrimsOnDisk(), $"{choice}: the saved trim is there");
                if (choice == ShareChoice.Upload)
                    Check.Contains(window.StatusText.Text, "not shared", "Upload went through ShareAsync (declined here)");
                else
                {
                    Check.Equal(1, seen.Count, $"{choice}: run once after the save");
                    Check.True(seen[0].Existed && seen[0].IsTrim && seen[0].Path.EndsWith("(trim).mp4"), $"{choice}: on the new trim file, not the source");
                }
            }
            finally { CopiumClient.HttpOverride = null; }
        }
    }

    [UnitTest]
    public static void A_cancelled_picker_keeps_the_trim_and_choosing_again_copies_it_without_trimming_again()
    {
        using var rig = new Rig(true, true);
        using var win = new Win(rig.Entry);
        var window = win.Window;
        window.Timeline.SelectionStart = 1;
        window.Timeline.SelectionEnd = 4;
        string target = Path.Combine(rig.Dir.File("elsewhere"), "copy.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // dialog cancelled: the trim is saved, no copy, the window stays open and usable
        rig.App.SaveTargetPicker = _ => null;
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.Equal(1, rig.TrimsOnDisk(), "the trim is saved");
        Check.False(File.Exists(target), "no copy");
        Check.True(Enabled(window), "halves enabled again");
        Check.Contains(window.StatusText.Text, "no copy saved. Choose again to share it.");
        // choose again: the copy is of the clip that was saved, and no second trim is made
        string? picked = null;
        rig.App.SaveTargetPicker = e => { picked = e.Path; return target; };
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.True(File.Exists(target), "the copy is written");
        Check.True(picked != null && picked.EndsWith("source (trim).mp4"), "the copy was taken from the first trim: " + picked);
        Check.Equal(1, rig.TrimsOnDisk(), "no second trim");
    }

    [UnitTest]
    public static void Any_share_action_reuses_the_saved_trim_after_a_failure_or_a_refusal()
    {
        using var rig = new Rig(true, true);
        using var win = new Win(rig.Entry);
        var window = win.Window;
        window.Timeline.SelectionStart = 1;
        window.Timeline.SelectionEnd = 4;
        var opened = new List<string>();
        bool opens = false;
        rig.App.ShareWindowOpener = e => { opened.Add(e.Path); return opens; };
        // Share file fails: trim stays, status says so
        Wait(window.SaveAsync(share: true, ShareChoice.ShareFile));
        Check.Equal(1, rig.TrimsOnDisk());
        Check.True(Enabled(window));
        Check.Contains(window.StatusText.Text, "Choose again to share it.");
        // a picker that throws: the catch re-enables, the trim is still the first one
        rig.App.SaveTargetPicker = _ => throw new InvalidOperationException("the dialog blew up");
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.True(Enabled(window), "enabled again after the picker threw");
        Check.Contains(window.StatusText.Text, "Choose again to share it.");
        Check.Equal(1, rig.TrimsOnDisk(), "no second trim");
        // a refused target (inside the clips folder): refused, still no second trim
        rig.App.SaveTargetPicker = _ => Path.Combine(rig.Dir.File("clips"), "copy.mp4");
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.True(Enabled(window));
        Check.Equal(1, rig.TrimsOnDisk(), "no second trim after a refusal");
        // the main half (Upload, declined) acts on the same clip too
        rig.App.ConsentPromptOverride = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow));
        Wait(window.SaveAsync(share: true));
        Check.Contains(window.StatusText.Text, "not shared. Choose again to share it.");
        Check.Equal(1, rig.TrimsOnDisk(), "the declined upload did not trim again");
        // and an upload that fails: buttons come back (safe now) and a retry uploads the same file
        var handler = new Offline();
        CopiumClient.HttpOverride = new HttpClient(handler);
        try
        {
            rig.App.ConsentPromptOverride = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.Enable));
            Wait(window.SaveAsync(share: true));
            Wait(window.SaveAsync(share: true));
            Check.Equal(2, handler.Requests, "both attempts reached the upload");
            Check.True(Enabled(window), "enabled after a failed upload");
            Check.Contains(window.StatusText.Text, "Choose again to retry.");
            Check.Equal(1, rig.TrimsOnDisk(), "the upload retry did not trim again");
        }
        finally { CopiumClient.HttpOverride = null; }
        // finally the share window opens: the first trim goes there, then the window closes
        opens = true;
        Wait(window.SaveAsync(share: true, ShareChoice.ShareFile));
        Check.Equal(1, rig.TrimsOnDisk(), "still one trim");
        Check.True(opened.Count >= 2 && opened.All(p => p == opened[0]) && opened[0].EndsWith("source (trim).mp4"), "every attempt shared the one saved trim");
        Check.True(win.Closed, "a completed choice closes the window");
    }

    [UnitTest]
    public static void Changing_the_selection_makes_the_next_share_trim_again_and_Save_trimmed_always_saves_anew()
    {
        using var rig = new Rig(true, true);
        using var win = new Win(rig.Entry);
        var window = win.Window;
        window.Timeline.SelectionStart = 1;
        window.Timeline.SelectionEnd = 4;
        var picked = new List<string>();
        rig.App.SaveTargetPicker = e => { picked.Add(Path.GetFileName(e.Path)); return null; };   // always cancelled: the trim stays
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.Equal(1, rig.TrimsOnDisk());
        // a changed out point: what a trim would write differs, so the saved one is not what is shared
        window.Timeline.SelectionEnd = 3.5;
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.Equal(2, rig.TrimsOnDisk(), "a new selection is trimmed again");
        // the new one is remembered in its turn
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.Equal(2, rig.TrimsOnDisk());
        // a changed in point as well
        window.Timeline.SelectionStart = 1.5;
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.Equal(3, rig.TrimsOnDisk(), "a new in point is trimmed again");
        // Save trimmed always makes a new trim and forgets the one waiting
        Wait(window.SaveAsync(share: false));
        Check.Equal(4, rig.TrimsOnDisk(), "Save trimmed is an explicit save");
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.Equal(5, rig.TrimsOnDisk(), "after Save trimmed the memory was gone: the share trims again");
        Check.Equal(new[] { "source (trim).mp4", "source (trim 2).mp4", "source (trim 2).mp4", "source (trim 3).mp4", "source (trim 5).mp4" }, picked.ToArray());
    }

    [UnitTest]
    public static void With_Replace_original_the_copy_is_taken_from_the_replaced_file_and_a_retry_does_not_trim_again()
    {
        using var rig = new Rig(true, true);
        using var win = new Win(rig.Entry);
        var window = win.Window;
        string source = rig.Entry.Path;
        long fullLength = new FileInfo(source).Length;
        rig.Entry.CopiumLink = "https://copium.example/old";
        window.Timeline.SelectionStart = 1;
        window.Timeline.SelectionEnd = 4;
        window.ReplaceCheck.IsChecked = true;
        string target = Path.Combine(rig.Dir.File("elsewhere"), "copy.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var got = new List<ClipEntry>();
        rig.App.SaveTargetPicker = e => { got.Add(e); return null; };   // cancelled: the replace has happened, the copy has not
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.True(got.Count == 1 && ReferenceEquals(got[0], rig.Entry), "the copy is of the (replaced) clip itself");
        Check.True(rig.Entry.CopiumLink == null, "the link of the old content is cleared");
        Check.Near(3, rig.Entry.Duration, 0.01, "the entry describes the trimmed file");
        long replacedLength = new FileInfo(source).Length;
        Check.True(replacedLength != fullLength, "the original was replaced by the shorter file");
        Check.Equal(0, rig.TrimsOnDisk(), "no separate trim file: it took the original's name");
        Check.True(Enabled(window), "enabled again");
        Check.False(window.ReplaceCheck.IsChecked == true, "the editor started over on the replaced file");
        Check.Contains(window.StatusText.Text, "no copy saved. Choose again to share it.");
        // choose again: acts on the replaced file, no second trim (the editor now shows the whole replaced clip)
        rig.App.SaveTargetPicker = e => { got.Add(e); return target; };
        Wait(window.SaveAsync(share: true, ShareChoice.SaveCopy));
        Check.Equal(2, got.Count);
        Check.True(ReferenceEquals(got[1], rig.Entry));
        Check.Equal(replacedLength, new FileInfo(source).Length, "the replaced file was not trimmed again");
        Check.Equal(replacedLength, new FileInfo(target).Length, "the copy is the replaced file");
        Check.Equal(0, rig.TrimsOnDisk(), "and still no extra trim file");
    }
    // ------------------------------------------------------------------ layout

    /// <summary>Lays the trim window's content out at its minimum size, offscreen, in the split state.</summary>
    internal static (TrimWindow Window, FrameworkElement Root) LayOutSplit(TrimWindow window, double width = MinWidth, double height = MinHeight)
    {
        window.UpdateShareButtons();
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        return (window, root);
    }

    [UnitTest]
    public static void The_split_button_fits_the_trim_windows_button_row_at_minimum_width_with_slack_and_no_overlap()
    {
        using var rig = new Rig(true, true);
        using var win = new Win(rig.Entry);
        var (window, root) = LayOutSplit(win.Window);
        Check.True(window.SaveShareSplit.Visibility == Visibility.Visible && window.SaveShareButton.Visibility != Visibility.Visible, "split state");
        double Left(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), root).X;
        double Right(FrameworkElement e) => Left(e) + e.ActualWidth;
        double bar = window.BottomBar.ActualWidth - window.BottomBar.Padding.Left - window.BottomBar.Padding.Right;
        Check.True(window.SaveButtons.DesiredSize.Width <= bar - RequiredSlack, $"the button row needs {window.SaveButtons.DesiredSize.Width:0.0} of {bar:0.0} px");
        // really arranged inside the bar: the arrow ends at the bar's content edge (the window's 1 px border and the bar's padding)
        Check.Near(MinWidth - 1 - window.BottomBar.Padding.Right, Right(window.SaveShareArrow), 0.6, "the arrow ends at the bar's right content edge");
        // left to right, nothing on top of anything
        Check.True(Right(window.ReplaceCheck) <= Left(window.SaveButton) + 0.01, "Replace original ends before Save trimmed");
        Check.True(Right(window.SaveButton) <= Left(window.SaveShareMain) + 0.01, "Save trimmed ends before Save & share");
        Check.Near(Right(window.SaveShareMain), Left(window.SaveShareArrow), 0.6, "the two halves touch");
        Check.True(Right(window.StatusText) <= Left(window.ReplaceCheck) + 0.01, "the status line ends before the buttons");
        Check.True(window.SaveShareMain.ActualWidth > 0 && window.SaveShareArrow.ActualWidth > 0, "both halves have a size");
    }

    // ------------------------------------------------------------------ pictures (CLINK_SHOTS=1, called by ToastShareLayoutTests)

    /// <summary>The whole trim window content, offscreen, in the split state, as a PNG for the current theme.</summary>
    internal static void Shots(string folder, string theme)
    {
        using var rig = new Rig(true, true);
        using var win = new Win(rig.Entry);
        var (_, root) = LayOutSplit(win.Window, 940, 720);
        ToastShareLayoutTests.SaveShot(root, folder, $"trim-split-{theme}");
    }

    // ------------------------------------------------------------------ rig

    private static void Wait(Task task)
    {
        ScreenshotUploadTests.Pump(() => task.IsCompleted, "the save", 30_000);
        task.GetAwaiter().GetResult();
    }

    /// <summary>A trim window that is always closed again (and so removed from TrimWindow's open list), whatever the test did.</summary>
    internal sealed class Win : IDisposable
    {
        public readonly TrimWindow Window;
        public bool Closed { get; private set; }

        public Win(ClipEntry entry)
        {
            Window = new TrimWindow(entry);
            Window.Closed += (_, _) => Closed = true;
        }

        public void Dispose()
        {
            try { if (!Closed) Window.Close(); }
            catch (Exception ex) { Log.Warn("test window close: " + ex.Message); }
        }
    }

    /// <summary>An app controller with a short synthetic clip in its clips folder, the Copium token and the cloud switch as asked.</summary>
    internal sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly AppController App = null!;
        public readonly ClipEntry Entry = null!;
        private readonly string? _override = Settings.DirOverride;
        private readonly string _clips = "";
        private readonly AppController? _previous = typeof(AppController).GetProperty(nameof(AppController.Instance))!.GetValue(null) as AppController;

        public Rig(bool token, bool cloud)
        {
            try
            {
                Settings.DirOverride = Dir.Path;
                _clips = Dir.File("clips");
                Directory.CreateDirectory(_clips);
                var settings = Settings.FromJson("{}");
                settings.ClipsFolder = _clips;
                settings.CloudSharingEnabled = cloud;
                if (token) settings.CopiumToken = "eup_token";
                App = new AppController(settings, new Updater(settings));
                string path = Path.Combine(_clips, "source.mp4");
                SyntheticClip.Write(path, 6);
                Entry = App.Library.Add(path, 6, new FileInfo(path).Length, null, Array.Empty<string>());
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public int TrimsOnDisk() => Directory.GetFiles(_clips, "*(trim*).mp4").Length;

        public void Dispose()
        {
            try
            {
                App?.Dispose();
                FlushQueuedSaves();
            }
            catch (Exception ex) { Log.Warn("test rig dispose: " + ex.Message); }
            finally
            {
                // the controller sets the static Instance and never clears it: put back the one the other tests left (a token here must not leak into them)
                typeof(AppController).GetProperty(nameof(AppController.Instance))!.GetSetMethod(true)!.Invoke(null, new object?[] { _previous });
                // only now, with nothing left that could write settings.json or clips.json: a save after this would land in the real folder
                Settings.DirOverride = _override;
                Dir.Dispose();
            }
        }

        /// <summary>Settings.Save from another thread is queued on the dispatcher and writes to Settings.Dir when it runs: let those run while the override still holds.</summary>
        private static void FlushQueuedSaves()
        {
            bool idle = false;
            Dispatcher.CurrentDispatcher.BeginInvoke(() => idle = true, DispatcherPriority.ApplicationIdle);
            ScreenshotUploadTests.Pump(() => idle, "queued saves", 5_000);
        }
    }
}
