using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clink.Screenshots;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Tests;

/// <summary>
/// Localization slice S2 (clips pane, library rows, selection bar, delete and share-files windows, and the texts of ClipDeleter /
/// BulkActions / LibraryView / ClipGrouping). Under <c>qps-ploc</c> the built (never shown) panes and windows are walked by
/// <see cref="L10nTreeHarness"/>: every string must come from the catalog. Plural sites are checked with synthetic ru / pl catalogs.
/// </summary>
internal static class L10nTreeClipsTests
{
    // ------------------------------------------------------------------ rig

    /// <summary>An app controller with a library in a scratch folder (so the pane has real rows) and the settings that switch the pane's features.</summary>
    private sealed class Env : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly Settings Settings;
        public readonly AppController App;
        private readonly string? _oldIndex = ClipLibrary.IndexPathOverride;

        public Env(bool token = true, bool cloud = true, string level = "local", string feature = "clips")
        {
            if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
            ClipLibrary.IndexPathOverride = Dir.File("clips.json");
            Settings = Settings.FromJson("{}");
            Settings.CloudSharingEnabled = cloud;
            if (token) Settings.CopiumToken = "test-token";
            Settings.LibraryLevel = level;
            Settings.LibraryFeature = feature;
            App = new AppController(Settings, new Updater(Settings));
        }

        public ClipEntry Clip(string file, string? game, bool shared = false, bool trim = false, string? source = null, bool fileGone = false)
        {
            string path = Dir.File(Path.Combine("clips", file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            var e = App.Library.Add(path, 42, 2_500_000, game, Array.Empty<string>(), isTrim: trim, sourcePath: source);
            if (shared) e.CopiumLink = "https://copium.pics/abc";
            if (fileGone) File.Delete(path);
            return e;
        }

        public void Dispose()
        {
            App.Dispose();
            ClipLibrary.IndexPathOverride = _oldIndex;
            Dir.Dispose();
        }
    }

    /// <summary>Texts that are not translatable on purpose, or that belong to another slice (named below). Each is commented.</summary>
    private static bool Allow(string text)
    {
        string t = text.Trim();
        // fake clip / game names and file names the rows show (user data), and a Copium link (a URL)
        if (t is "Neon Drift" or "Neon Drift (trim)" or "Clip.mp4" or "shot-1.png" || t.StartsWith("https://", StringComparison.Ordinal)) return true;
        return false;   // (the Share split button's labels and tips now come from the catalog; the "▾" glyph has no letter)
    }

    private static void Arrange(UIElement ui)
    {
        ui.Measure(new Size(1200, 900));
        ui.Arrange(new Rect(0, 0, 1200, 900));
        ui.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static void NoneUnlocalized(DependencyObject root, string what)
    {
        var bad = L10nTreeHarness.Unlocalized(root, Allow);
        Check.Equal(Array.Empty<string>(), bad.ToArray(), what + " (layout error: " + L10nTreeHarness.LastLayoutError + ")");
    }

    private static void NoMissing(string what) => Check.Equal(Array.Empty<string>(), L.Missing.OrderBy(k => k, StringComparer.Ordinal).ToArray(), what + ": keys missing from the catalog");

    private static void Marked(string? text, string what)
    {
        Check.True(!string.IsNullOrEmpty(text) && PseudoLocale.IsPseudo(text), what + ": not from the catalog: \"" + text + "\"");
        Check.True(L10nTreeHarness.IsAllowed(L10nTreeHarness.StripFrames(text!)) || Allow(L10nTreeHarness.StripFrames(text!)), what + ": has a hard-coded part: \"" + text + "\"");
    }

    // ------------------------------------------------------------------ the pane

    [UnitTest]
    public static void The_clips_pane_local_list_with_trims_and_the_share_split_button_is_localized()
    {
        using var env = new Env();
        var parent = env.Clip("a.mp4", "Neon Drift", shared: true);
        env.Clip("a (trim).mp4", "Neon Drift", trim: true, source: parent.Path);
        env.Clip("a (trim 2).mp4", "Neon Drift", trim: true, source: parent.Path);
        env.Clip("b.mp4", null);
        env.Clip("c.mp4", "Neon Drift");
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var pane = new ClipsPane();
        // open the clip that has trims, so the trim rows (indented, with the guide line) are in the tree too
        var expanded = (HashSet<string>)typeof(ClipsPane).GetField("_expandedClips", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pane)!;
        pane.Refresh();
        expanded.Add(ClipGrouping.Key(parent.Path));
        pane.Refresh();
        NoneUnlocalized(pane, "the Local clips list");
        Check.Equal(5, pane.ClipLocalList.Items.Count, "the clip with trims opened shows its 2 trims");
        Marked((string)pane.LocalLevel.Content, "the Local segment");
        Marked((string)pane.SharedLevel.Content, "the Shared segment");
        NoMissing("clips pane");
    }

    [UnitTest]
    public static void The_trimmed_filter_and_the_shared_level_are_localized()
    {
        using var env = new Env();
        var parent = env.Clip("a.mp4", "Neon Drift", shared: true);
        env.Clip("a (trim).mp4", "Neon Drift", trim: true, source: parent.Path);
        env.Clip("gone.mp4", "Neon Drift", shared: true, fileGone: true);   // shared, local file deleted
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var pane = new ClipsPane();
        pane.Refresh();
        pane.TrimmedChip.IsChecked = true;   // the Trimmed filter
        Arrange(pane);
        NoneUnlocalized(pane, "the Trimmed filter");

        var shared = new ClipsPane();
        env.Settings.LibraryLevel = "shared";
        shared.Refresh();
        Check.Equal(2, shared.ClipSharedList.Items.Count, "the two shared clips");
        NoneUnlocalized(shared, "the Shared clips list");
        NoMissing("clips pane levels");
    }

    [UnitTest]
    public static void The_empty_texts_of_every_list_are_localized()
    {
        using var env = new Env();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        foreach (var feature in new[] { LibraryFeature.Clips, LibraryFeature.Screenshots })
            foreach (var level in new[] { LibraryLevel.Local, LibraryLevel.Shared })
                foreach (bool trimmed in new[] { false, true })
                    Marked(LibraryView.EmptyText(feature, level, trimmed && feature == LibraryFeature.Clips), $"empty text {feature}/{level}/{trimmed}");
        // the pane shows it: an empty library, every clips combination
        foreach (var (level, chip) in new[] { ("local", false), ("local", true), ("shared", false), ("shared", true) })
        {
            env.Settings.LibraryLevel = level;
            var pane = new ClipsPane();
            pane.Refresh();
            if (chip) pane.TrimmedChip.IsChecked = true;
            Arrange(pane);
            Check.True(pane.EmptyText.Visibility == Visibility.Visible, "the empty text shows (" + level + ", chip " + chip + ")");
            Marked(pane.EmptyText.Text, "pane empty text " + level + "/" + chip);
            NoneUnlocalized(pane, "the empty pane " + level + "/" + chip);
        }
        NoMissing("empty texts");
    }

    [UnitTest]
    public static void The_screenshot_rows_and_their_buttons_are_localized()
    {
        using var env = new Env();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var pane = new ClipsPane();
        pane.Refresh();
        var now = new DateTime(2026, 10, 25, 14, 3, 0);
        ScreenshotRow Row(string name, string? link, bool exists, string? game = null) => new(new ScreenshotItem(
            new ScreenshotEntry { Path = Path.Combine(env.Dir.Path, name), Created = now, Width = 1920, Height = 1080, Size = 412_000, Game = game, CopiumLink = link, CopiumPageUrl = link },
            exists, 1920, 1080));
        var rows = new System.Collections.ObjectModel.ObservableCollection<ScreenshotRow>
        {
            Row("shot-1.png", null, true),                                       // local: Edit, Open, folder, Upload, Delete
            Row("shot-2.png", "https://copium.pics/s/abc", true, "Neon Drift"),  // shared: Copy link, Open page
            Row("shot-3.png", "https://copium.pics/s/def", false),              // shared, file gone: Remove from list
        };
        pane.ShotList.ItemsSource = rows;
        pane.ShotList.Visibility = Visibility.Visible;
        pane.ClipLocalList.Visibility = Visibility.Collapsed;
        pane.EmptyText.Visibility = Visibility.Collapsed;
        Arrange(pane);
        NoneUnlocalized(pane, "the Screenshots list");
        foreach (var r in rows)
        {
            Marked(r.SelectName, "select name");
            if (r.Entry.Game == null) Marked(r.Title, "screenshot title"); else Check.Equal("Neon Drift", r.Title, "a game name is user data");
        }
        // the confirm texts and the problem lines of a screenshot delete
        foreach (bool hasFile in new[] { true, false })
            foreach (var link in new[] { null, "https://copium.pics/s/abc" })
                Marked(ScreenshotRow.ConfirmText(new ScreenshotEntry { Path = @"C:\shots\shot-1.png", CopiumLink = link }, hasFile), "confirm text");
        foreach (var result in Enum.GetValues<ScreenshotDeleteResult>())
            foreach (bool fixedDrive in new[] { true, false })
                if (result != ScreenshotDeleteResult.Deleted) Marked(ScreenshotRow.DeleteProblemText(result, fixedDrive), "delete problem " + result);
        Marked(ScreenshotRow.RemoveHint, "remove hint");
        NoMissing("screenshots");
    }

    [UnitTest]
    public static void The_selection_bar_with_clips_selected_is_localized()
    {
        using var env = new Env();
        var parent = env.Clip("a.mp4", "Neon Drift", shared: true);
        env.Clip("a (trim).mp4", "Neon Drift", trim: true, source: parent.Path);
        env.Clip("b.mp4", "Neon Drift");
        env.Clip("c.mp4", null);
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var pane = new ClipsPane();
        pane.Refresh();
        Arrange(pane);
        var boxes = Descendants<CheckBox>(pane.ClipLocalList).ToList();
        Check.True(boxes.Count >= 3, "the row check boxes exist: " + boxes.Count);
        foreach (var b in boxes.Take(3)) b.IsChecked = true;   // the model follows the box (Select_Changed)
        Arrange(pane);
        Check.True(pane.SelectBar.Visibility == Visibility.Visible, "the selection bar is up");
        Marked(pane.SelectCount.Text, "the count");
        Check.True(pane.SelectActions.Children.OfType<Button>().Count() >= 2, "the bar has its buttons");
        NoneUnlocalized(pane, "the selection bar (3 clips, signed in)");

        // signed out, cloud sharing off, and a single clip: the other button sets
        foreach (var (token, cloud) in new[] { (false, true), (false, false), (true, false) })
        {
            env.Settings.CopiumToken = token ? "test-token" : null;
            env.Settings.CloudSharingEnabled = cloud;
            var p2 = new ClipsPane();
            p2.Refresh();
            Arrange(p2);
            foreach (var b in Descendants<CheckBox>(p2.ClipLocalList).Take(1)) b.IsChecked = true;
            Arrange(p2);
            NoneUnlocalized(p2, $"the selection bar (token {token}, cloud {cloud})");
        }
        NoMissing("selection bar");
    }

    [UnitTest]
    public static void The_share_menu_of_the_bar_and_the_rows_carries_catalog_text()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        foreach (bool screenshots in new[] { false, true })
            foreach (var (count, linked) in new[] { (3, 0), (3, 3), (3, 1), (101, 0) })
            {
                var items = BulkActions.ShareMenu(true, true, count, linked, uploadOn: true, screenshots)!;
                Check.True(items.Count >= 2, "menu items");
                foreach (var item in items)
                {
                    Marked(item.Header, "menu header " + item.Action);
                    Marked(item.ToolTip, "menu tip " + item.Action);
                }
            }
        Marked(BulkActions.TooManyToShareText, "too many");
        Marked(BulkActions.SplitMainName, "split name");
        Marked(BulkActions.SplitMainTip, "split tip");
        Marked(BulkActions.UploadStoppingText, "stopping");
        Marked(BulkActions.UploadLabel, "upload label");
        Marked(BulkActions.CopyLinksLabel, "copy links label");
        Marked(BulkActions.ShareFilesLabel, "share files label");
        foreach (bool shots in new[] { false, true }) Marked(BulkActions.UploadTip(shots), "upload tip");
        NoMissing("share menu");
    }

    // ------------------------------------------------------------------ the windows

    [UnitTest]
    public static void The_delete_confirm_window_in_all_its_variants_is_localized()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        var plain = new ClipDeleteConfirmWindow(ClipDeleter.ConfirmBody("Neon Drift", hasTrims: true, shared: true), null);
        NoneUnlocalized(plain, "Delete clip? (plain)");
        Marked(plain.Title, "plain title");

        var withTrims = new ClipDeleteConfirmWindow(ClipDeleter.ConfirmBodyWithTrims("Neon Drift", 2, shared: true), null, 2, null);
        NoneUnlocalized(withTrims, "Delete clip? (with trims)");
        Marked((string)withTrims.WithTrimsButton.Content, "the clip-and-trims button");
        var oneTrim = new ClipDeleteConfirmWindow(ClipDeleter.ConfirmBodyWithTrims("Neon Drift", 1, shared: false), null, 1, null);
        NoneUnlocalized(oneTrim, "Delete clip? (one trim)");

        string blockedText = ClipDeleter.TrimsBlockedText(ClipDeleteResult.Uploading)!;
        var blocked = new ClipDeleteConfirmWindow(ClipDeleter.ConfirmBodyWithTrims("Neon Drift", 2, shared: false), null, 2, blockedText);
        Check.True(blocked.BlockedText.Visibility == Visibility.Visible, "the blocked line shows");
        NoneUnlocalized(blocked, "Delete clip? (blocked trims)");

        var bulk = new ClipDeleteConfirmWindow(ClipDeleter.BulkConfirmTitle(3, false), ClipDeleter.BulkConfirmBody(3, 1, 2, 1, false), L.T("Delete"), null);
        NoneUnlocalized(bulk, "Delete 3 clips?");
        Marked(bulk.Title, "bulk title");
        var bulkShots = new ClipDeleteConfirmWindow(ClipDeleter.BulkConfirmTitle(2, true), ClipDeleter.BulkConfirmBody(2, 1, 0, 1, true, 1), L.T("Delete"), null);
        NoneUnlocalized(bulkShots, "Delete 2 screenshots?");
        NoMissing("delete window");
    }

    [UnitTest]
    public static void The_share_files_window_is_localized()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var dir = new TempDir();
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        ShareFileItem[] Items(int n) => Enumerable.Range(1, n).Select(i =>
        {
            string p = dir.File($"clip{i}.mp4");
            File.WriteAllBytes(p, new byte[] { 1 });
            return new ShareFileItem(p, false, 3_400_000, 30);
        }).ToArray();
        foreach (var (n, shots) in new[] { (1, false), (3, false), (12, false), (2, true) })
        {
            var w = new ShareFilesWindow(Items(n), shots);
            NoneUnlocalized(w, $"Share files ({n}, screenshots {shots})");
            Marked(w.Title, "title");
            Marked(w.NameText.Text, "the card's name line");
        }
        // an archived file still being looked for, and the same file once it was not reached
        var looking = new ShareFilesWindow(new[] { new ShareFileItem(@"\\nas\clips\x.mp4", true, 1000, 1) }, false);
        NoneUnlocalized(looking, "Share files (looking)");
        NoMissing("share files window");
    }

    // ------------------------------------------------------------------ result texts carry the pseudo marker

    [UnitTest]
    public static void The_result_texts_of_ClipDeleter_carry_the_catalog_marker()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        foreach (var r in Enum.GetValues<ClipDeleteResult>())
        {
            if (r != ClipDeleteResult.Deleted) Marked(ClipDeleter.ProblemText(r), "ProblemText " + r);
            Marked(ClipDeleter.TrimsBlockedText(r) ?? (r == ClipDeleteResult.Deleted ? "[!! x !!]" : null), "TrimsBlockedText " + r);
            foreach (bool onTrim in new[] { false, true })
                foreach (int deleted in new[] { 0, 2 })
                    Marked(ClipDeleter.TreeProblemText(new ClipDeleter.TreeOutcome(r, deleted, 3, onTrim)), $"TreeProblemText {r}/{onTrim}/{deleted}");
            foreach (int kept in new[] { 1, 3 })
                Marked(ClipDeleter.BulkProblemText(2, 2 + kept, r), $"BulkProblemText {r}/{kept}");
        }
        Marked(ClipDeleter.TrimsBlockedText(null, oneDeleting: true), "one trim deleting");
        Marked(ClipDeleter.BulkProblemText(1, 4, null), "BulkProblemText without a reason");
        Check.Equal("", ClipDeleter.BulkProblemText(4, 4, null), "everything went: nothing to say");
        foreach (bool shots in new[] { false, true })
            foreach (int n in new[] { 1, 6 })
            {
                Marked(ClipDeleter.BulkConfirmTitle(n, shots), "BulkConfirmTitle");
                Marked(ClipDeleter.BulkConfirmBody(n, 1, shots ? 0 : 2, 3, shots, shots ? 2 : 0), "BulkConfirmBody");
            }
        foreach (int n in new[] { 1, 4 }) Marked(ClipDeleter.WithoutFileText(n), "WithoutFileText");
        foreach (var skip in Enum.GetValues<ClipDeleter.ShotSkip>())
            if (skip is ClipDeleter.ShotSkip.Uploading or ClipDeleter.ShotSkip.NoFile or ClipDeleter.ShotSkip.Refused or ClipDeleter.ShotSkip.NotLocal)
                Marked(ClipDeleter.ShotSkipText(skip), "ShotSkipText " + skip);
        foreach (var (deleted, total, without) in new[] { (0, 3, 0), (1, 1, 0), (3, 3, 0), (2, 5, 0), (1, 3, 2), (0, 2, 2) })
            Marked(ClipDeleter.ShotBulkSummary(deleted, total, without), $"ShotBulkSummary {deleted}/{total}/{without}");
        Marked(ClipDeleter.ConfirmBody("Neon Drift", true, true), "ConfirmBody");
        Marked(ClipDeleter.ConfirmBodyWithTrims("Neon Drift", 1, true), "ConfirmBodyWithTrims");
        Marked(SaveCopy.OwnFileText, "OwnFileText");
        Marked(SaveCopy.InsideClipsText, "InsideClipsText");
        Marked(SaveCopy.TargetProblem("C:\\x\\a|b.mp4".Replace("|", "\0"), @"C:\x\c.mp4", null), "TargetProblem invalid name");
        NoMissing("ClipDeleter texts");
    }

    [UnitTest]
    public static void The_result_texts_of_BulkActions_carry_the_catalog_marker()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        foreach (bool shots in new[] { false, true })
        {
            foreach (int n in new[] { 1, 6 }) { Marked(BulkActions.CountLabel(n, shots), "CountLabel"); Marked(BulkActions.ShareWindowTitle(n, shots), "ShareWindowTitle"); }
            foreach (bool cancelled in new[] { false, true })
                foreach (var (up, linked, total, refused, busy, failed, error, stop, notTried, notCopied, retry) in new[]
                {
                    (3, 0, 3, 0, 0, 0, (string?)null, (string?)null, 0, false, false),
                    (1, 0, 1, 0, 0, 0, null, null, 0, false, false),
                    (2, 1, 5, 1, 1, 1, "Disk full", null, 0, false, false),
                    (1, 0, 4, 0, 0, 3, "Server said no", "Network lost", 2, true, true),
                    (0, 0, 2, 0, 0, 0, null, null, 0, false, false),
                    (0, 0, 2, 2, 1, 0, "No route", "Stopped here", 1, false, false),
                    (1, 2, 3, 0, 0, 0, null, "Token expired", 1, true, false),
                })
                    Marked(BulkActions.UploadSummary(up, linked, total, refused, busy, failed, error, cancelled, shots, stop, notTried, notCopied, retry), $"UploadSummary {up}/{linked}/{total}");
            Marked(BulkActions.UploadStatus(new BulkShareResult(0, 0, 0, Array.Empty<string>(), 0, 0, 0, null, false, true), shots), "UploadStatus declined");
            foreach (bool withPercent in new[] { false, true })
                Marked(BulkActions.UploadingStatus(2, 6, withPercent ? 0.4 : -1), "UploadingStatus");
        }
        foreach (var (saved, total, error, unreachable, busy) in new[] { (3, 3, (string?)null, 0, 0), (1, 1, null, 0, 0), (2, 5, "Disk full", 1, 1), (0, 4, null, 2, 0), (0, 3, "Locked", 0, 3), (1, 3, null, 0, 0) })
            Marked(BulkActions.SaveSummary(saved, total, error, unreachable, busy), $"SaveSummary {saved}/{total}");
        foreach (var (present, total) in new[] { (0, 1), (0, 3), (1, 2), (2, 5) }) Marked(BulkActions.ShareFilesSummary(present, total), $"ShareFilesSummary {present}/{total}");
        Check.Null(BulkActions.ShareFilesSummary(3, 3), "nothing left out: nothing to say");
        NoMissing("BulkActions texts");
    }

    [UnitTest]
    public static void The_row_labels_and_library_texts_carry_the_catalog_marker()
    {
        using var scope = L.Scope("qps-ploc");
        L.ClearMissing();
        foreach (var name in new[] { "Local", "Shared" })
            foreach (int n in new[] { 0, 7 })
                Marked(LibraryView.Label(name, n), "segment " + name + n);
        foreach (int n in new[] { 1, 3 })
        {
            Marked(ClipGrouping.TrimsLabel(n), "TrimsLabel");
            foreach (bool open in new[] { false, true }) Marked(ClipGrouping.ToggleName(open, n, "Neon Drift"), "ToggleName");
        }
        foreach (var (what, ok) in new[] { ("share", true), ("save", true), ("trim", true), ("open its folder", true), ("play it", true) })
            Marked(ClipRow.CannotText(what), "CannotText " + what);
        Marked(ClipRow.ArchiveOffline, "ArchiveOffline");
        Marked(ClipRow.DeletedFromArchiveText, "DeletedFromArchiveText");
        Marked(ClipRow.LocalDeleted, "LocalDeleted");
        Marked(ClipRow.LookingText, "LookingText");
        Marked(ClipRow.LinkCopiedText, "LinkCopiedText");
        Marked(ClipRow.SavingDeleteText, "SavingDeleteText");
        foreach (var outcome in new[] { ShareOutcome.Declined, ShareOutcome.Failed })
            foreach (bool cloud in new[] { true, false })
                Marked(ClipsPane.ShareStatusFor(false, outcome, null, outcome == ShareOutcome.Failed ? "no route" : null, cloud).Status, "ShareStatusFor " + outcome);
        Marked(ClipsPane.ShareStatusFor(true, ShareOutcome.Uploaded, null, null, true).Status, "ShareStatusFor ok");
        NoMissing("row labels");
    }

    // ------------------------------------------------------------------ plural forms under a synthetic ru / pl catalog

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

    /// <summary>Forms that name their category, so the test sees which one the number picked.</summary>
    private static Dictionary<string, string> Cats(string tag, bool withCount = true) =>
        new[] { "one", "few", "many", "other" }.ToDictionary(c => c, c => $"{tag}-{c}" + (withCount ? " {count}" : ""));

    private static readonly (int N, string Ru, string Pl)[] s_counts =
    {
        (1, "one", "one"), (2, "few", "few"), (5, "many", "many"), (21, "one", "many"),   // CLDR: ru 21 = one; pl 21 = many
    };

    [UnitTest]
    public static void The_plural_sites_pick_the_right_form_in_ru_and_pl()
    {
        var keys = new[]
        {
            "{count} trims", "Show {count} trims of {title}", "Hide {count} trims of {title}", "{count} clips selected", "{count} screenshots selected",
            "Share {count} clips", "Share {count} screenshots", "Delete {count} clips?", "Delete {count} screenshots?",
            "{count} without a file were left. Use Remove from list for them.", "{count} files could not be reached and were left out.",
            "Saved {count} copies.", "Saved {saved} of {count} copies.",
            "{count} clips will be moved to the Recycle Bin, where you can restore them.",
            "Deleted {count} screenshots.", "Uploaded {count} clips.", "Deleted {deleted} of {total}. {count} were kept.",
        };
        string Id(string key) => "k" + Array.IndexOf(keys, key);
        foreach (var locale in new[] { "ru", "pl" })
        {
            using var catalog = Synthetic(locale, keys.ToDictionary(k => k, k => Cats(Id(k))));
            using var scope = L.Scope(locale);
            foreach (var (n, ru, pl) in s_counts)
            {
                string cat = locale == "ru" ? ru : pl;
                string Want(string key) => $"{Id(key)}-{cat} {n}";
                string at = $" ({locale}, {n})";
                Check.Equal(Want("{count} trims"), ClipGrouping.TrimsLabel(n), "TrimsLabel" + at);
                Check.Equal(Want("Show {count} trims of {title}"), ClipGrouping.ToggleName(false, n, "T"), "ToggleName show" + at);
                Check.Equal(Want("Hide {count} trims of {title}"), ClipGrouping.ToggleName(true, n, "T"), "ToggleName hide" + at);
                Check.Equal(Want("{count} clips selected"), BulkActions.CountLabel(n, false), "CountLabel clips" + at);
                Check.Equal(Want("{count} screenshots selected"), BulkActions.CountLabel(n, true), "CountLabel screenshots" + at);
                Check.Equal(Want("Share {count} clips"), BulkActions.ShareWindowTitle(n, false), "ShareWindowTitle clips" + at);
                Check.Equal(Want("Share {count} screenshots"), BulkActions.ShareWindowTitle(n, true), "ShareWindowTitle screenshots" + at);
                Check.Equal(Want("Delete {count} clips?"), ClipDeleter.BulkConfirmTitle(n, false), "BulkConfirmTitle clips" + at);
                Check.Equal(Want("Delete {count} screenshots?"), ClipDeleter.BulkConfirmTitle(n, true), "BulkConfirmTitle screenshots" + at);
                Check.Equal(Want("{count} without a file were left. Use Remove from list for them."), ClipDeleter.WithoutFileText(n), "WithoutFileText" + at);
                Check.Equal(Want("Saved {count} copies."), BulkActions.SaveSummary(n, n, null), "SaveSummary all saved" + at);
                if (n > 1) Check.Equal(Want("Saved {saved} of {count} copies."), BulkActions.SaveSummary(1, n, null), "SaveSummary some saved" + at);
                Check.Equal(Want("{count} files could not be reached and were left out."), BulkActions.ShareFilesSummary(1, 1 + n), "ShareFilesSummary" + at);
                Check.Equal(Want("{count} clips will be moved to the Recycle Bin, where you can restore them."), ClipDeleter.BulkConfirmBody(n, 0, 0, 0, false), "BulkConfirmBody" + at);
                Check.Equal(Want("Deleted {count} screenshots."), ClipDeleter.ShotBulkSummary(n, n, 0), "ShotBulkSummary" + at);
                Check.Equal(Want("Deleted {deleted} of {total}. {count} were kept."), ClipDeleter.BulkProblemText(0, n, null), "BulkProblemText" + at);
                Check.True(BulkActions.UploadSummary(n, 0, n, 0, 0, 0, null, false, false).StartsWith(Want("Uploaded {count} clips.") + " ", StringComparison.Ordinal), "UploadSummary head" + at);
            }
        }
        NoMissing("plural sites");
    }
    [UnitTest]
    public static void The_share_files_window_and_the_clip_delete_window_pick_plural_forms_in_ru()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var dir = new TempDir();
        using var catalog = Synthetic("ru", new Dictionary<string, Dictionary<string, string>>
        {
            ["{count} files"] = Cats("files"),
            ["Delete clip and {count} trims"] = Cats("clip-and-trims"),
        });
        using var scope = L.Scope("ru");
        foreach (var (n, ru, _) in s_counts)
        {
            var items = Enumerable.Range(1, n).Select(i => { string p = dir.File($"ru{n}-{i}.mp4"); File.WriteAllBytes(p, new byte[] { 1 }); return new ShareFileItem(p, false, 0, 0); }).ToArray();
            var w = new ShareFilesWindow(items, false);
            Check.Equal($"files-{ru} {n}", w.NameText.Text, $"the card's name line for {n} files");
            var d = new ClipDeleteConfirmWindow("Body", null, n, null);
            Check.Equal($"clip-and-trims-{ru} {n}", (string)d.WithTrimsButton.Content, $"the clip-and-trims button for {n} trims");
        }
    }

    // ------------------------------------------------------------------ English stays what it was

    [UnitTest]
    public static void English_texts_of_the_plural_and_composed_sites_are_what_they_were()
    {
        using var scope = L.Scope("en");
        Check.Equal("1 trim", ClipGrouping.TrimsLabel(1));
        Check.Equal("3 trims", ClipGrouping.TrimsLabel(3));
        Check.Equal("Show 3 trims of Game", ClipGrouping.ToggleName(false, 3, "Game"));
        Check.Equal("Hide 1 trim of Game", ClipGrouping.ToggleName(true, 1, "Game"));
        Check.Equal("1 clip selected", BulkActions.CountLabel(1, false));
        Check.Equal("6 clips selected", BulkActions.CountLabel(6, false));
        Check.Equal("3 screenshots selected", BulkActions.CountLabel(3, true));
        Check.Equal("Share 1 clip", BulkActions.ShareWindowTitle(1, false));
        Check.Equal("Share 6 screenshots", BulkActions.ShareWindowTitle(6, true));
        Check.Equal("Delete 1 clip?", ClipDeleter.BulkConfirmTitle(1, false));
        Check.Equal("Delete 3 screenshots?", ClipDeleter.BulkConfirmTitle(3, true));
        Check.Equal("1 clip will be moved to the Recycle Bin, where you can restore it. The shared links keep working. 1 trim of a selected clip is not selected: it stays in the list and moves to the top level. 2 can't be deleted right now and will be skipped.",
            ClipDeleter.BulkConfirmBody(1, 1, 1, 2, false));
        Check.Equal("3 screenshots will be moved to the Recycle Bin, where you can restore them. The links stay on Copium. 1 without a file was left. Use Remove from list for it.",
            ClipDeleter.BulkConfirmBody(3, 2, 0, 0, true, 1));
        Check.Equal("Neon has 2 trims. Delete just the full clip, or the clip and its trims? Everything goes to the Recycle Bin, where you can restore it. The shared link keeps working.",
            ClipDeleter.ConfirmBodyWithTrims("Neon", 2, true));
        Check.Equal("Neon has 1 trim. Delete just the full clip, or the clip and its trim? Everything goes to the Recycle Bin, where you can restore it.",
            ClipDeleter.ConfirmBodyWithTrims("Neon", 1, false));
        Check.Equal("Its trims can't be deleted right now: one is uploading.", ClipDeleter.TrimsBlockedText(ClipDeleteResult.Uploading));
        Check.Equal("Nothing was deleted: one of its trims couldn't be moved to the Recycle Bin.", ClipDeleter.TreeProblemText(new ClipDeleter.TreeOutcome(ClipDeleteResult.Failed, 0, 3, true)));
        Check.Equal("Deleted 2 of 3 files. One of its trims is on a network or removable drive. The clip and the remaining trims were kept.",
            ClipDeleter.TreeProblemText(new ClipDeleter.TreeOutcome(ClipDeleteResult.NotLocal, 2, 3, true)));
        Check.Equal("Deleted 1 of 3 files. The clip is open in the trim window, so it was kept.", ClipDeleter.TreeProblemText(new ClipDeleter.TreeOutcome(ClipDeleteResult.TrimOpen, 1, 3)));
        Check.Equal("Deleted 4 of 6. 2 were kept: one is uploading.", ClipDeleter.BulkProblemText(4, 6, ClipDeleteResult.Uploading));
        Check.Equal("Deleted 3 of 4. 1 was kept: it can't be reached.", ClipDeleter.BulkProblemText(3, 4, ClipDeleteResult.Unreachable));
        Check.Equal("Deleted 3 of 4. 1 was kept.", ClipDeleter.BulkProblemText(3, 4, null));
        Check.Equal("Deleted 1 screenshot.", ClipDeleter.ShotBulkSummary(1, 1, 0));
        Check.Equal("Nothing was deleted. 2 without a file were left. Use Remove from list for them.", ClipDeleter.ShotBulkSummary(0, 2, 2));
        Check.Equal("Uploaded 3 clips. 3 links are on the clipboard.", BulkActions.UploadSummary(3, 0, 3, 0, 0, 0, null, false, false));
        Check.Equal("Uploaded 1 of 3 screenshots. 1 link is ready but couldn't be copied to the clipboard. 1 is too large to share. 1 was busy. 1 failed. Stopped: Network lost. 2 were not tried. Cancelled. Use Copy links to try again.",
            BulkActions.UploadSummary(1, 0, 3, 1, 1, 1, null, true, true, "Network lost.", 2, true, true));
        Check.Equal("Nothing was uploaded. 3 failed: Disk full.", BulkActions.UploadSummary(0, 0, 3, 0, 0, 3, "Disk full.", false, false));
        Check.Equal("Saved 1 copy.", BulkActions.SaveSummary(1, 1, null));
        Check.Equal("Saved 3 copies.", BulkActions.SaveSummary(3, 3, null));
        Check.Equal("Saved 2 of 5 copies. Disk full. 1 could not be reached. 2 were busy.", BulkActions.SaveSummary(2, 5, "Disk full.", 1, 2));
        Check.Equal("Nothing was saved.", BulkActions.SaveSummary(0, 4, null));
        Check.Equal("The file could not be reached.", BulkActions.ShareFilesSummary(0, 1));
        Check.Equal("None of the files could be reached.", BulkActions.ShareFilesSummary(0, 3));
        Check.Equal("1 file could not be reached and was left out.", BulkActions.ShareFilesSummary(2, 3));
        Check.Equal("2 files could not be reached and were left out.", BulkActions.ShareFilesSummary(1, 3));
        Check.Equal("Local", LibraryView.Label("Local", 0));
        Check.Equal("Shared 12", LibraryView.Label("Shared", 12));
        Check.Equal("Select up to 100 files to share at once", BulkActions.TooManyToShareText);
        Check.Equal("Uploading 2 of 6… 40%", BulkActions.UploadingStatus(2, 6, 0.4).Replace(" %", "%"));
        Check.Equal("Uploading 2 of 6…", BulkActions.UploadingStatus(2, 6));
        Check.Equal("Move 'a.png' to the Recycle Bin? The link stays on Copium. If it cannot go to the Recycle Bin, Windows asks again before anything is deleted for good.",
            ScreenshotRow.ConfirmText(new ScreenshotEntry { Path = @"C:\s\a.png", CopiumLink = "https://x" }, true));
        Check.Equal("Remove 'a.png' from the list? Its file is already gone.", ScreenshotRow.ConfirmText(new ScreenshotEntry { Path = @"C:\s\a.png" }, false));
        Check.Equal("archive offline: cannot trim right now", ClipRow.CannotText("trim"));
        Check.Equal("archive offline: cannot open its folder right now", ClipRow.CannotText("open its folder"));
        NoMissing("English");
    }

    // ------------------------------------------------------------------ repair round 1: key shapes

    private static Dictionary<string, Dictionary<string, string>> Forms(string tag, string[] cats, params string[] keys) =>
        keys.ToDictionary(k => k, k => cats.ToDictionary(c => c, c => $"{tag}{Array.IndexOf(keys, k)}-{c} {{count}}"), StringComparer.Ordinal);

    [UnitTest]
    public static void M1_key_names_and_the_limit_are_placeholders_and_English_is_unchanged()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var dir = new TempDir();
        using (L.Scope("en"))
        {
            string p = dir.File("a.mp4"); File.WriteAllBytes(p, new byte[] { 1 });
            var w = new ShareFilesWindow(new[] { new ShareFileItem(p, false, 10, 1) }, false);
            Check.Equal("Close (Esc)", (string)w.CloseIconButton.ToolTip);
            Check.Equal("Copy the files, then paste them into Discord or a folder with Ctrl+V (Ctrl+C in this window does the same)", (string)w.CopyButton.ToolTip);
            Check.Equal("Discord takes up to 10 files per message", w.DiscordText.Text);
            Check.Equal("File copied. Paste with Ctrl+V into Discord or a folder.", ShareFilesWindow.CopiedText(1));
            Check.Equal("Files copied. Paste with Ctrl+V into Discord or a folder.", ShareFilesWindow.CopiedText(3));
            Check.Equal("No screenshots yet — press PrintScreen", LibraryView.EmptyText(LibraryFeature.Screenshots, LibraryLevel.Local, false));
            Check.Equal("No screenshots yet — press F12", LibraryView.EmptyText(LibraryFeature.Screenshots, LibraryLevel.Local, false, "F12"));
            var pane = new ClipsPane();
            Check.Equal("Clear selection (Esc)", (string)pane.SelectClearButton.ToolTip);
            Check.Equal("Clear", (string)pane.SelectClearButton.Content);
            Check.Equal("Trimmed", (string)pane.TrimmedChip.Content);
            Check.Equal("Local", (string)pane.LocalLevel.Content);
        }
        // a translation moves the key names where its sentence wants them
        var tr = new Dictionary<string, object>
        {
            ["Close ({keys})"] = "[{keys}] zakryt",
            ["Clear selection ({keys})"] = "{keys}: sbros",
            ["No screenshots yet — press {keys}"] = "Nazhmite {keys}",
            ["Copy the files, then paste them into Discord or a folder with {paste} ({copy} in this window does the same)"] = "{copy}/{paste}",
        };
        using var catalog2 = L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "all", JsonSerializer.Serialize(L.Embedded.English.Entries.ToDictionary(kv => kv.Key, kv => kv.Value.Text != null ? (object)kv.Value.Text : kv.Value.Forms!))),
            new CatalogSource("ru", "t", JsonSerializer.Serialize(tr)),
        }));
        using var scope = L.Scope("ru");
        string p2 = dir.File("b.mp4"); File.WriteAllBytes(p2, new byte[] { 1 });
        var w2 = new ShareFilesWindow(new[] { new ShareFileItem(p2, false, 10, 1) }, false);
        Check.Equal("[Esc] zakryt", (string)w2.CloseIconButton.ToolTip);
        Check.Equal("Ctrl+C/Ctrl+V", (string)w2.CopyButton.ToolTip);
        Check.Equal("{keys}: sbros".Replace("{keys}", "Esc"), (string)new ClipsPane().SelectClearButton.ToolTip);
        Check.Equal("Nazhmite F12", LibraryView.EmptyText(LibraryFeature.Screenshots, LibraryLevel.Local, false, "F12"));
    }

    [UnitTest]
    public static void M2_File_copied_is_two_plain_keys_not_a_plural_in_ru()
    {
        var plain = new Dictionary<string, object>
        {
            ["File copied. Paste with {keys} into Discord or a folder."] = "one-file {keys}",
            ["Files copied. Paste with {keys} into Discord or a folder."] = "many-files {keys}",
        };
        using var catalog = L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "all", JsonSerializer.Serialize(L.Embedded.English.Entries.ToDictionary(kv => kv.Key, kv => kv.Value.Text != null ? (object)kv.Value.Text : kv.Value.Forms!))),
            new CatalogSource("ru", "t", JsonSerializer.Serialize(plain)),
        }));
        using var scope = L.Scope("ru");
        foreach (int n in new[] { 1, 2, 5, 21, 101 })
            Check.Equal(n == 1 ? "one-file Ctrl+V" : "many-files Ctrl+V", ShareFilesWindow.CopiedText(n), "files copied for " + n);   // 21 is ru 'one' but still several files
    }

    [UnitTest]
    public static void L1_the_plural_category_follows_the_number_that_is_shown()
    {
        using var catalog = Synthetic("ru", Forms("u", new[] { "one", "few", "many", "other" }, "Uploaded {count} clips.", "Uploaded {count} screenshots.", "Uploaded {uploaded} of {count} clips."));
        using var scope = L.Scope("ru");
        // 21 uploaded of a 5-file plan (more than asked, so the head shows {count}=21): the form is ru 'one' (21), not 'many' (5)
        Check.True(BulkActions.UploadSummary(21, 0, 5, 0, 0, 0, null, false, false).StartsWith("u0-one 21 ", StringComparison.Ordinal), "clips head follows 21");
        Check.True(BulkActions.UploadSummary(22, 0, 1, 0, 0, 0, null, false, true).StartsWith("u1-few 22 ", StringComparison.Ordinal), "screenshots head follows 22");
        // "{uploaded} of {count}": the noun agrees with {count}, which is the number of files in the plan
        Check.True(BulkActions.UploadSummary(2, 0, 21, 0, 0, 0, null, false, false).StartsWith("u2-one 21 ", StringComparison.Ordinal), "'x of count' follows count");
    }

    [UnitTest]
    public static void M4_the_no_game_sentences_are_whole_keys_and_English_is_unchanged()
    {
        using (L.Scope("en"))
        {
            Check.Equal("Clip will be moved to the Recycle Bin, where you can restore it. Its trims stay in the list. The shared link keeps working.", ClipDeleter.ConfirmBody(null, true, true));
            Check.Equal("Trimmed clip will be moved to the Recycle Bin, where you can restore it.", ClipDeleter.ConfirmBody(null, false, false, isTrim: true));
            Check.Equal("Neon will be moved to the Recycle Bin, where you can restore it.", ClipDeleter.ConfirmBody("Neon", false, false));
            Check.Equal("Clip has 2 trims. Delete just the full clip, or the clip and its trims? Everything goes to the Recycle Bin, where you can restore it.", ClipDeleter.ConfirmBodyWithTrims(null, 2, false));
            Check.Equal("Trimmed clip has 1 trim. Delete just the full clip, or the clip and its trim? Everything goes to the Recycle Bin, where you can restore it. The shared link keeps working.", ClipDeleter.ConfirmBodyWithTrims(null, 1, true, isTrim: true));
            Check.Equal("Show 3 trims of Clip", ClipGrouping.ToggleName(false, 3, null));
            Check.Equal("Hide 1 trim of Clip", ClipGrouping.ToggleName(true, 1, null));
            Check.Equal("Show 2 trims of Trimmed clip", ClipGrouping.ToggleName(false, 2, null, isTrim: true));
            Check.Equal("Hide 2 trims of Trimmed clip", ClipGrouping.ToggleName(true, 2, null, isTrim: true));
        }
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var env = new Env();
        var noGame = new ClipRow(env.Clip("n.mp4", null));
        var game = new ClipRow(env.Clip("g.mp4", "Neon Drift"));
        var trim = new ClipRow(env.Clip("t.mp4", null, trim: true));
        Check.Null(noGame.GameTitle, "no game: no title for a {title} slot");
        Check.Null(trim.GameTitle);
        Check.Equal("Neon Drift", game.GameTitle);
        // the generic word never goes through a {title} key: a catalog that has only the whole-sentence keys is used
        var tr = new Dictionary<string, object>
        {
            ["Clip will be moved to the Recycle Bin, where you can restore it."] = "WHOLE-CLIP",
            ["{title} will be moved to the Recycle Bin, where you can restore it."] = "GAME {title}",
        };
        using var catalog = L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "all", JsonSerializer.Serialize(L.Embedded.English.Entries.ToDictionary(kv => kv.Key, kv => kv.Value.Text != null ? (object)kv.Value.Text : kv.Value.Forms!))),
            new CatalogSource("ru", "t", JsonSerializer.Serialize(tr)),
        }));
        using var scope = L.Scope("ru");
        Check.Equal("WHOLE-CLIP", ClipDeleter.ConfirmBody(noGame.GameTitle, false, false));
        Check.Equal("GAME Neon Drift", ClipDeleter.ConfirmBody(game.GameTitle, false, false));
    }

    [UnitTest]
    public static void L5_an_unknown_segment_name_throws_and_the_context_keys_carry_the_English()
    {
        bool threw = false;
        try { LibraryView.Label("Archive", 3); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check.True(threw, "no fragment fallback");
        using (L.Scope("en"))
        {
            Check.Equal("Local 12", LibraryView.Label("Local", 12));
            Check.Equal("Shared", LibraryView.Label("Shared", 0));
            Check.Equal("Uploaded", L.T("screenshot|Uploaded"));
            Check.Equal("Trimmed", L.T("filter|Trimmed"));
            Check.Equal("Clear", L.T("selection|Clear"));
        }
        var tr = new Dictionary<string, object> { ["level|Local"] = "Lokalnye", ["level|Shared"] = "Obshchie", ["level|Local {count}"] = "Lokalnye ({count})" };
        using var catalog = L.UseCatalogs(CatalogSet.Build(new[]
        {
            new CatalogSource("en", "all", JsonSerializer.Serialize(L.Embedded.English.Entries.ToDictionary(kv => kv.Key, kv => kv.Value.Text != null ? (object)kv.Value.Text : kv.Value.Forms!))),
            new CatalogSource("ru", "t", JsonSerializer.Serialize(tr)),
        }));
        using var scope = L.Scope("ru");
        Check.Equal("Lokalnye", LibraryView.Label("Local", 0));
        Check.Equal("Lokalnye (4)", LibraryView.Label("Local", 4));
        Check.Equal("Obshchie", LibraryView.Label("Shared", 0));
    }

    [UnitTest]
    public static void The_plural_sites_use_all_six_forms_in_a_synthetic_Arabic_catalog()
    {
        string[] cats = { "zero", "one", "two", "few", "many", "other" };
        var keys = new[] { "{count} trims", "{count} clips selected", "Share {count} clips", "Delete {count} clips?", "Select up to {count} files to share at once" };
        using var catalog = Synthetic("ar", Forms("a", cats, keys));
        using var scope = L.Scope("ar");
        foreach (var (n, cat) in new[] { (0, "zero"), (1, "one"), (2, "two"), (3, "few"), (11, "many"), (100, "other") })
        {
            string at = " (ar, " + n + ")";
            Check.Equal($"a0-{cat} {n}", ClipGrouping.TrimsLabel(n), "TrimsLabel" + at);
            Check.Equal($"a1-{cat} {n}", BulkActions.CountLabel(n, false), "CountLabel" + at);
            Check.Equal($"a2-{cat} {n}", BulkActions.ShareWindowTitle(n, false), "ShareWindowTitle" + at);
            Check.Equal($"a3-{cat} {n}", ClipDeleter.BulkConfirmTitle(n, false), "BulkConfirmTitle" + at);
        }
        Check.Equal("a4-other 100", BulkActions.TooManyToShareText, "the limit goes through the plural machinery (100 is 'other' in ar)");
    }

    // ------------------------------------------------------------------ every key the slice's sources ask for is in clips.json / core.json, and every clips.json key is used

    [UnitTest]
    public static void Every_key_in_the_slice_sources_is_in_clips_or_core_json_and_every_clips_key_is_used()
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return;   // not run from a checkout: nothing to read
        var clips = L10nSourceScan.AreaKeys(root, "clips");
        var core = L10nSourceScan.AreaKeys(root, "core");
        var problems = new List<string>();
        var hits = L10nSourceScan.CSharp(root, new[]
        {
            @"UI\ClipsPane.xaml.cs", @"UI\ClipsPane.Selection.cs", @"UI\LibraryView.cs", @"UI\ClipItem.cs", @"UI\ClipGrouping.cs",
            @"UI\ClipDeleteConfirmWindow.xaml.cs", @"UI\ShareFilesWindow.xaml.cs", @"Services\ClipDelete.cs", @"Services\BulkActions.cs", @"Services\SaveCopy.cs",
        }, new[] { L10nSourceScan.T, L10nSourceScan.Plural });
        hits.AddRange(L10nSourceScan.Xaml(root, new[] { @"UI\ClipsPane.xaml", @"UI\ClipDeleteConfirmWindow.xaml", @"UI\ShareFilesWindow.xaml" }));
        var used = hits.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var h in hits)
            if (!clips.Contains(h.Key) && !core.Contains(h.Key)) problems.Add(h.File + ": " + h.Key);
        int found = hits.Count;
        Check.True(found > 200, "found the literals of the slice's sources: " + found);
        Check.Equal(Array.Empty<string>(), problems.ToArray(), "keys the sources ask for that clips.json / core.json lack");
        var unused = clips.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Check.Equal(Array.Empty<string>(), unused, "clips.json keys no source of the slice uses");
    }

    [UnitTest]
    public static void A_row_keeps_its_more_precise_file_problem_and_clears_it_when_the_file_is_back()
    {
        using var env = new Env();
        var row = new ClipRow(env.Clip("p.mp4", "Neon Drift"));
        row.SetProblem(ClipRow.CannotText("trim"));
        row.SetFileProblem(ClipRow.ArchiveOffline);   // the plain line must not replace the precise one
        Check.Equal(ClipRow.CannotText("trim"), row.Status);
        row.SetFileProblem(ClipRow.LocalDeleted);     // another file problem does
        Check.Equal(ClipRow.LocalDeleted, row.Status);
        row.ClearFileProblem();
        Check.Null(row.Status);
        row.SetProblem(ClipRow.CannotText("play it"));
        row.ClearFileProblem();
        Check.Null(row.Status, "a refused-click line is a file problem too");
        row.Status = ClipRow.LinkCopiedText;
        row.IsProblem = true;
        row.ClearFileProblem();
        Check.Equal(ClipRow.LinkCopiedText, row.Status, "another status is left alone");
    }
}
