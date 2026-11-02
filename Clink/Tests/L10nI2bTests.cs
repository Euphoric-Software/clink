using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using Clink.Engine;
using Clink.Screenshots;
using Clink.Screenshots.Editing;
using Clink.Screenshots.Imaging;
using Clink.Services;
using Clink.Services.Localization;
using Clink.UI;
using Clink.UI.Screenshots;

namespace Clink.Tests;

/// <summary>
/// Integration step I2b (the last key-shape changes before translation): the texts a log gets stay English whatever the language is, while
/// the user sees the language of the UI (<see cref="Note"/> producers and the notices said through them); a failure is told by its type or
/// kind, never by the words of a message; the generic words of "Select ..." and the new-capture save prompt are whole sentences.
/// </summary>
internal static class L10nI2bTests
{
    private static string Shown(Note note) { using (L.Scope("qps-ploc")) return note.Shown; }
    private static string English(Note note) { using (L.Scope("qps-ploc")) return note.English; }

    /// <summary>Under qps-ploc the note shows from the catalog, its English text is what <c>en</c> shows, and no pseudo text reaches it.</summary>
    private static void Both(Note note, string english, string what)
    {
        using (L.Scope("en")) Check.Equal(english, note.Shown, what + " (en)");
        Check.Equal(english, English(note), what + ": the log text is English whatever the language is");
        Check.False(PseudoLocale.IsPseudo(English(note)), what + ": no pseudo text in the log text");
        Check.True(PseudoLocale.IsPseudo(Shown(note)), what + ": shown from the catalog: " + Shown(note));
    }

    // ------------------------------------------------------------------ producers return notes

    [UnitTest]
    public static void The_bulk_summaries_are_notes_whose_log_text_is_english_and_whose_screen_text_is_translated()
    {
        L.ClearMissing();
        // the reason is a note of its own: it is worded in each language, not carried over translated
        var reason = Note.Of("The folder is no longer there");
        Both(BulkActions.SaveSummaryNote(2, 5, reason, unreachable: 2, busy: 2),
            "Saved 2 of 5 copies. The folder is no longer there. 2 could not be reached. 2 were busy.", "SaveSummaryNote");
        Both(BulkActions.SaveSummaryNote(0, 3, (Note?)null), "Nothing was saved.", "SaveSummaryNote (nothing)");
        Both(BulkActions.UploadSummaryNote(2, 1, 5, refused: 2, busy: 0, failed: 2, firstError: Note.Of("Copium rejected the token"), cancelled: false, screenshots: false, stopError: Note.Of("Clink is closing"), notTried: 0),
            "Uploaded 2 of 4 clips. 3 links are on the clipboard. 2 are too large to share. 2 failed: Copium rejected the token. Stopped: Clink is closing.", "UploadSummaryNote");
        // the plain-text forms are the same sentences
        using (L.Scope("en"))
        {
            Check.Equal(BulkActions.SaveSummaryNote(1, 1, (Note?)null).Shown, BulkActions.SaveSummary(1, 1, null));
            Check.Equal("Saved 1 of 2 copies. disk on fire.", BulkActions.SaveSummary(1, 2, "disk on fire"));
        }
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    [UnitTest]
    public static void The_recording_texts_are_notes_and_a_size_is_written_with_the_units_of_the_language_it_is_shown_in()
    {
        L.ClearMissing();
        Both(RecordingPolicy.StatusLineNote(75, "Neon Drift"), "Recording 01:15 · Neon Drift", "StatusLineNote");
        Both(RecordingPolicy.SavedSummaryNote(754, 1_234_000_000), "12:34 · 1.2 GB", "SavedSummaryNote");
        Both(RecordingPolicy.RecoveredTitleNote(3), "Recovered 3 recordings", "RecoveredTitleNote");
        var failed = new RecordingResult(@"C:\Clips\r.mp4", 12, 1000, null, Array.Empty<string>(), RecordingEndReason.WriteFailed, "disk on fire", 0);
        var (title, text) = RecordingPolicy.ProblemNote(failed)!.Value;
        Both(title, "Recording stopped", "problem title");
        Both(text, "Writing the file failed: disk on fire The recording was recovered and saved.", "problem text");
        Check.Null(RecordingPolicy.ProblemNote(failed with { Reason = RecordingEndReason.UserStopped }), "a recording that ended well has no problem");
        // the size inside a note: the unit follows the language on screen, English in the log
        string shown = Shown(Note.Of("{elapsed} · {size}", ("elapsed", "0:01"), ("size", Note.Size(5_000_000))));
        Check.True(PseudoLocale.IsPseudo(shown) && shown.Contains("5", StringComparison.Ordinal), "the size follows the language: " + shown);
        Check.Equal("0:01 · 5 MB", English(Note.Of("{elapsed} · {size}", ("elapsed", "0:01"), ("size", Note.Size(5_000_000)))));
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    [UnitTest]
    public static void The_save_copy_target_problem_the_import_notices_and_the_consent_notice_are_notes()
    {
        L.ClearMissing();
        Both(SaveCopy.TargetProblemNote(@"C:\a\x.mp4", @"C:\a\x.mp4", null)!, "That is the clip's own file", "TargetProblemNote (own file)");
        Both(SaveCopy.TargetProblemNote(@"C:\Clips\x.mp4", @"D:\y.mp4", @"C:\Clips")!, "Choose a folder outside Clink's clips folder", "TargetProblemNote (inside the clips folder)");
        Check.Null(SaveCopy.TargetProblemNote(@"E:\out\x.mp4", @"D:\y.mp4", @"C:\Clips"));
        Both(AppDataRedirect.NoticeNoteFor("Notes"), "Clink was started from inside Notes: Windows keeps a private copy of its settings for that app, so changes made now are not seen by Clink started from Explorer or the Start menu", "AppDataRedirect notice");
        Both(ArchiveService.FullPathHintNote, @"Use a full path such as D:\Clips or \\nas\clips", "full path hint");
        // the cloud-off notice is said through notes: the log gets English, the toast the language
        var settings = Settings.FromJson("{}");
        settings.CloudSharingEnabled = false;
        var told = new List<(Note Title, Note Text)>();
        var gate = new ConsentGate(settings) { Notice = (_, title, text) => told.Add((title, text)) };
        Check.False(gate.EnsureAsync(ConsentContext.ClipShare).GetAwaiter().GetResult(), "cloud sharing is off: no");
        Check.Equal(1, told.Count);
        Both(told[0].Title, "Cloud sharing is off", "consent notice title");
        Both(told[0].Text, "Cloud sharing is off — enable it under App → Cloud sharing", "consent notice text");
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    [UnitTest]
    public static void A_pending_clip_merge_notice_and_an_import_result_are_worded_again_in_english_for_the_log()
    {
        using var folder = new TempDir();
        File.WriteAllText(folder.File("clips.json"), "{ this is not json");
        L.ClearMissing();
        try
        {
            SettingsImport.ForgetPendingClipMerge();
            var merge = typeof(SettingsImport).GetMethod("MergeClipIndex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            using (L.Scope("qps-ploc")) merge.Invoke(null, new object[] { folder.Path });
            Note note = SettingsImport.TakeClipsNoticeNote() ?? throw new TestFailure("the failed merge left a notice");
            Check.True(PseudoLocale.IsPseudo(Shown(note)), "the notice the user reads: " + Shown(note));
            string english = English(note);
            Check.True(english.StartsWith("Settings imported; the clip list could not be merged: the other copy's clip list cannot be read (", StringComparison.Ordinal), "English in the log: " + english);
            Check.False(PseudoLocale.IsPseudo(english), "no pseudo text in the log text: " + english);
            Check.Null(SettingsImport.TakeClipsNoticeNote(), "said once");
        }
        finally { SettingsImport.ForgetPendingClipMerge(); SettingsImport.TakeClipsNotice(); }
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    // ------------------------------------------------------------------ Say / SayT: the log is English, the user's text is translated

    [UnitTest]
    public static void A_notice_said_from_the_window_and_the_library_logs_english_and_shows_the_language()
    {
        if (Application.Current == null) throw new TestFailure("needs the running application (run it with Clink.exe --unittest)");
        using var dir = new TempDir();
        var oldIndex = ClipLibrary.IndexPathOverride;
        ClipLibrary.IndexPathOverride = dir.File("clips.json");
        var settings = Settings.FromJson("{}");
        var app = new AppController(settings, new Updater(settings));
        try
        {
            var shown = new List<string>();
            app.Notice += shown.Add;
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Collect(string line) => lines.Enqueue(line);
            Log.Line += Collect;
            try
            {
                using (L.Scope("qps-ploc"))
                {
                    // the shapes of the converted MainWindow / ClipsPane sites: a key, a key with a path (marked left to right), a key with a system error text
                    app.SayT("Clips folder changed");
                    app.SayT("{file} is gone (deleted or moved outside Clink)", ("file", Note.Ltr("a.mp4")));
                    app.SayT("Could not open folder: {error}", ("error", "Access is denied."));
                    app.Say(ArchiveService.FullPathHintNote);
                    app.Say(BulkActions.SaveSummaryNote(1, 2, Note.Of("The folder is no longer there")));
                }
            }
            finally { Log.Line -= Collect; }
            var log = lines.ToArray();
            Check.Equal(5, shown.Count);
            foreach (string s in shown) Check.True(PseudoLocale.IsPseudo(s), "shown from the catalog: " + s);
            foreach (string expected in new[]
            {
                "notice: Clips folder changed", "notice: a.mp4 is gone (deleted or moved outside Clink)", "notice: Could not open folder: Access is denied.",
                @"notice: Use a full path such as D:\Clips or \\nas\clips", "notice: Saved 1 of 2 copies. The folder is no longer there.",
            })
                Check.True(log.Any(l => l.EndsWith(expected, StringComparison.Ordinal)), "the log has: " + expected + " in " + string.Join(" | ", log.Where(l => l.Contains("notice:"))));
            Check.False(log.Any(l => l.Contains("notice:") && PseudoLocale.IsPseudo(l)), "and nothing of the pseudo-locale reached the log");
        }
        finally { app.Dispose(); ClipLibrary.IndexPathOverride = oldIndex; }
    }

    [UnitTest]
    public static void The_window_and_the_library_never_hand_a_translated_string_to_Say()
    {
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        var bad = new List<string>();
        foreach (string file in new[] { "MainWindow.xaml.cs", @"UI\ClipsPane.xaml.cs", @"UI\ClipsPane.Selection.cs" })
        {
            int n = 0;
            foreach (string line in File.ReadAllLines(Path.Combine(root, file)))
            {
                n++;
                // Say(L.T(..)), Say("..."), Say(string expression) would log what the UI language made of it; L.Ltr inside a note is Note.Ltr
                if (Regex.IsMatch(line, @"\bSay\(\s*(?:L\.(?:T|Plural)\(|\$?@?"")")) bad.Add(file + ":" + n + ": " + line.Trim());
                if (Regex.IsMatch(line, @"\bSayT\(.*L\.Ltr\(")) bad.Add(file + ":" + n + " (L.Ltr in a note): " + line.Trim());
            }
        }
        Check.Equal(Array.Empty<string>(), bad.ToArray(), "use SayT / Say(Note) with Note.Ltr");
        // the other callers pass a Note
        string selection = File.ReadAllText(Path.Combine(root, @"UI\ClipsPane.Selection.cs"));
        Check.Contains(selection, "App.Say(ClipDeleter.ShotBulkSummaryNote(", "the screenshot delete summary is a note");
        Check.False(selection.Contains("App.Say(problem.Length", StringComparison.Ordinal), "the clip delete summary is a note");
    }

    // ------------------------------------------------------------------ the updater's download reasons

    [UnitTest]
    public static void A_download_failure_reason_is_a_note_english_in_the_log_and_translated_on_screen()
    {
        L.ClearMissing();
        const string url = "https://cdn.example/Clink-0.4.2.exe";
        Both(Updater.DownloadErrorNote(new HttpRequestException("EVIL", null, HttpStatusCode.ServiceUnavailable), url), "HTTP 503", "HTTP status");
        Both(Updater.DownloadErrorNote(new IOException("EVIL", new System.Net.Sockets.SocketException()), url), "network error (cdn.example)", "network error");
        Both(Updater.DownloadErrorNote(new UnauthorizedAccessException("EVIL"), url), "the download could not be saved in the updates folder", "not saved");
        Both(Updater.DownloadErrorNote(new FormatException("EVIL"), url), "unexpected error (details in the log)", "unexpected");
        Both(Updater.DownloadErrorNote(new TaskCanceledException("EVIL"), url), "no answer within 30 s", "no answer");
        // the reasons DownloadAsync attaches to its own exceptions (Updater.DownloadAsync) are keys too
        foreach (string key in new[]
        {
            "update|the offer carries no signed SHA-256", "update|size mismatch", "update|SHA-256 mismatch", "update|the downloaded file is not a Windows executable",
            "update|download stalled (no data for {seconds} s)", "update|download took too long (over {seconds} s)", "update|download too slow (under {rate} KB/s)",
            "update|the download server did not answer within {seconds} s", "update|the downloaded exe does not report v{version}", "update|unknown error",
        })
            Check.True(L.Catalogs.English.Entries.ContainsKey(key), "the English catalog has \"" + key + "\"");
        // one such reason inside the sentence around it: each side keeps its own language
        var sentence = Note.Of("downloading v{version} failed: {reason}", ("version", "0.4.2"), ("reason", Note.Of("update|download stalled (no data for {seconds} s)", ("seconds", "60"))));
        Both(sentence, "downloading v0.4.2 failed: download stalled (no data for 60 s)", "reason inside a sentence");
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    // ------------------------------------------------------------------ a failure is told by its type or kind

    [UnitTest]
    public static void Already_recording_and_not_enough_space_are_told_by_type_and_the_low_disk_sentence_carries_the_bytes()
    {
        L.ClearMissing();
        var already = new AlreadyRecordingException();
        Check.True(already is InvalidOperationException, "still an InvalidOperationException for every other handler");
        Check.Equal("Already recording", already.Message);
        var low = new NotEnoughFreeSpaceException("Not enough free disk space to record (111 MB free, 1024 MB needed)", 111L * 1_048_576 + 5, 1L << 30);
        Check.Equal(111L * 1_048_576 + 5, low.FreeBytes);
        Check.Equal(1L << 30, low.NeededBytes);
        Both(AppController.NotEnoughSpaceNote(low), "Not enough free space to record. Not enough free disk space to record (111 MB free, 1024 MB needed)", "low disk sentence");
        Check.Equal(low.Message, "Not enough free disk space to record (111 MB free, 1024 MB needed)");
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        string recording = File.ReadAllText(Path.Combine(root, @"Services\AppController.Recording.cs"));
        Check.Contains(recording, "catch (AlreadyRecordingException", "the type picks the branch");
        Check.False(recording.Contains("Contains(\"Already recording\"", StringComparison.Ordinal), "no logic on the message text");
        Check.False(File.ReadAllText(Path.Combine(root, @"Engine\Engine.cs")).Contains("new InvalidOperationException(\"Already recording\")", StringComparison.Ordinal), "the engine throws the type");
        Check.False(File.ReadAllText(Path.Combine(root, @"Engine\RecordingSink.cs")).Contains("new InvalidOperationException(\"Already recording\")", StringComparison.Ordinal), "so does the sink");
        var notRunning = new EngineNotRunningException();
        Check.True(notRunning is InvalidOperationException, "still an InvalidOperationException for every other handler");
        Check.Equal("Not recording: the engine is not running", notRunning.Message);
        Check.Contains(recording, "catch (EngineNotRunningException", "the type picks the branch");
        Check.False(File.ReadAllText(Path.Combine(root, @"Engine\Engine.cs")).Contains("new InvalidOperationException(\"Not recording: the engine", StringComparison.Ordinal), "the engine throws the type");
        // the two race-only refusals are catalog notes: translated on screen, English in the log
        Both(Note.Of("Already recording"), "Already recording", "already recording");
        Both(Note.Of("Not recording: the engine is not running"), "Not recording: the engine is not running", "engine not running");
        Check.Contains(recording, "return Note.Of(\"Already recording\")", "the race shows the catalog note");
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    [UnitTest]
    public static void A_trim_refused_for_alignment_is_a_typed_exception_mapped_to_the_catalog_notes()
    {
        L.ClearMissing();
        var untrusted = new TrimAlignmentException(TrimAlignmentRefusal.Untrusted);
        var older = new TrimAlignmentException(TrimAlignmentRefusal.OlderClink);
        Check.True(untrusted is InvalidOperationException, "still an InvalidOperationException");
        Check.Equal(ClipAlignment.UntrustedNote, untrusted.Message);
        Check.Equal(ClipAlignment.OlderClinkNote, older.Message);
        Both(TrimWindow.AlignmentRefusalNote(untrusted.Kind), ClipAlignment.UntrustedNote, "untrusted");
        Both(TrimWindow.AlignmentRefusalNote(older.Kind), ClipAlignment.OlderClinkNote, "older Clink");
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        string engine = File.ReadAllText(Path.Combine(root, @"Engine\Trimmer.cs"));
        Check.False(engine.Contains("new InvalidOperationException(ClipAlignment.", StringComparison.Ordinal), "the trimmer throws the type");
        Check.Contains(File.ReadAllText(Path.Combine(root, @"UI\TrimWindow.xaml.cs")), "catch (TrimAlignmentException", "the window maps the kind");
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    [UnitTest]
    public static void The_selection_bar_acts_by_the_kind_of_a_share_button_not_by_its_label()
    {
        foreach (string locale in new[] { "en", "qps-ploc" })
        {
            using var scope = L.Scope(locale);
            var split = BulkActions.ShareButtons(hasToken: true, cloudOn: true, count: 2, linked: 2);
            Check.Equal(1, split.Count);
            Check.Equal(BulkBarButtonKind.Split, split[0].Kind);
            var plain = BulkActions.ShareButtons(hasToken: false, cloudOn: true, count: 2, linked: 2);
            Check.Equal(new[] { BulkBarButtonKind.ShareFiles, BulkBarButtonKind.CopyLinks }, plain.Select(b => b.Kind).ToArray(), locale);
            Check.Equal(BulkActions.ShareButtonLabels(false, true, 2, 2).ToArray(), plain.Select(b => b.Label).ToArray(), "the labels are the same list");
            Check.Equal(new[] { BulkBarButtonKind.CopyLinks }, BulkActions.ShareButtons(false, false, 2, 2).Select(b => b.Kind).ToArray(), "cloud off, all linked");
        }
        using (L.Scope("qps-ploc")) Check.True(PseudoLocale.IsPseudo(BulkActions.ShareButtons(false, true, 1, 0)[0].Label), "the label is translated, the kind is not");
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        string selection = File.ReadAllText(Path.Combine(root, @"UI\ClipsPane.Selection.cs"));
        Check.False(selection.Contains("label == BulkActions.", StringComparison.Ordinal), "no comparison with a translated label");
    }

    [UnitTest]
    public static void A_refusal_to_upload_is_worded_by_its_kind_with_no_text_cut_from_the_refusal()
    {
        // (the kinds themselves are checked on real refusals in ShareCopiumFitTests: the account's limit, a copy that stayed too big,
        // a copy over the length limit, a clip that cannot fit, and a game in front)
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        string trim = File.ReadAllText(Path.Combine(root, @"UI\TrimWindow.xaml.cs"));
        Check.False(trim.Contains("ToLowerInvariant(refusal[0])", StringComparison.Ordinal), "no re-casing of a refusal");
        Check.False(trim.Contains("L.Code == \"en\"", StringComparison.Ordinal), "no branch on the language");
    }

    // ------------------------------------------------------------------ whole sentences for the generic words

    [UnitTest]
    public static void The_accessible_name_of_a_row_checkbox_is_a_whole_sentence_for_the_generic_titles()
    {
        L.ClearMissing();
        string date = new DateTime(2026, 10, 12, 14, 3, 0).ToString("MMM d, HH:mm", L.FormatCulture);
        var created = new DateTime(2026, 10, 12, 14, 3, 0);
        using (L.Scope("en"))
        {
            Check.Equal("Select Clip, " + date, new ClipRow(new ClipEntry { Path = @"C:\c\a.mp4", Created = created }).SelectName);
            Check.Equal("Select Trimmed clip, " + date, new ClipRow(new ClipEntry { Path = @"C:\c\a (trim).mp4", Created = created, IsTrim = true }).SelectName);
            Check.Equal("Select Neon Drift, " + date, new ClipRow(new ClipEntry { Path = @"C:\c\a.mp4", Created = created, Game = "Neon Drift" }).SelectName);
            Check.Equal("Select Screenshot, " + date, new ScreenshotRow(new ScreenshotItem(new ScreenshotEntry { Path = @"C:\s\a.png", Created = created }, true, 10, 10)).SelectName);
            Check.Equal("Select Neon Drift, " + date, new ScreenshotRow(new ScreenshotItem(new ScreenshotEntry { Path = @"C:\s\a.png", Created = created, Game = "Neon Drift" }, true, 10, 10)).SelectName);
        }
        using (L.Scope("qps-ploc"))
        {
            foreach (string name in new[]
            {
                new ClipRow(new ClipEntry { Path = @"C:\c\a.mp4", Created = created }).SelectName,
                new ClipRow(new ClipEntry { Path = @"C:\c\a (trim).mp4", Created = created, IsTrim = true }).SelectName,
                new ScreenshotRow(new ScreenshotItem(new ScreenshotEntry { Path = @"C:\s\a.png", Created = created }, true, 10, 10)).SelectName,
            })
            {
                Check.True(PseudoLocale.IsPseudo(name), "from the catalog: " + name);
                Check.False(name.Contains("Clip", StringComparison.Ordinal) || name.Contains("Screenshot", StringComparison.Ordinal), "the generic word is inside the sentence, not a slot filled with English: " + name);
            }
        }
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every text is in a catalog");
    }

    // ------------------------------------------------------------------ the new-capture save prompt

    [UnitTest]
    public static void The_save_prompt_names_a_new_capture_differently_from_the_changes_to_a_saved_screenshot()
    {
        L.ClearMissing();
        using (L.Scope("en"))
        {
            Check.Equal("Save this screenshot?", ScreenshotEditorWindow.SavePromptText(unsavedNewCapture: true, onQuit: false));
            Check.Equal("Save this screenshot before Clink quits?", ScreenshotEditorWindow.SavePromptText(unsavedNewCapture: true, onQuit: true));
            Check.Equal("Save the changes to this screenshot?", ScreenshotEditorWindow.SavePromptText(unsavedNewCapture: false, onQuit: false));
            Check.Equal("Save the changes to this screenshot before Clink quits?", ScreenshotEditorWindow.SavePromptText(unsavedNewCapture: false, onQuit: true));
            // the language restart asks the same question in its own words (a restart, not a quit)
            Check.Equal("Save this screenshot before Clink restarts?", ScreenshotEditorWindow.SavePromptText(unsavedNewCapture: true, onQuit: true, onRestart: true));
            Check.Equal("Save the changes to this screenshot before Clink restarts?", ScreenshotEditorWindow.SavePromptText(unsavedNewCapture: false, onQuit: true, onRestart: true));
            Check.Equal("Save this screenshot?", ScreenshotEditorWindow.SavePromptText(unsavedNewCapture: true, onQuit: false, onRestart: true), "a close is never a restart");
        }
        using (L.Scope("qps-ploc"))
        {
            var seen = new HashSet<string>();
            foreach (bool isNew in new[] { true, false })
                foreach (bool onQuit in new[] { true, false })
                    foreach (bool onRestart in new[] { true, false })
                    {
                        string text = ScreenshotEditorWindow.SavePromptText(isNew, onQuit, onRestart);
                        Check.True(PseudoLocale.IsPseudo(text), $"from the catalog (new {isNew}, quit {onQuit}, restart {onRestart})");
                        if (onQuit) seen.Add(text);
                    }
            Check.Equal(4, seen.Count, "new capture / saved screenshot, quit / restart: four different questions");
        }
        Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "all six are in Locales/en/editor.json");
    }

    [UnitTest]
    public static void The_editor_asks_the_new_capture_question_until_the_capture_has_been_saved()
    {
        bool saves = false;   // what the next SaveAsync answers
        EditorRequest Make(bool isNew) => new()
        {
            Document = DocumentState.Create(new BgraImage(40, 30)),
            Title = "t",
            IsNewCapture = isNew,
            Options = new ScreenshotEditorOptions(),
            SaveAsync = (_, _, _) => Task.FromResult(saves),
        };
        var flag = typeof(ScreenshotEditorWindow).GetProperty("HoldsUnsavedNewCapture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new TestFailure("no HoldsUnsavedNewCapture");
        // the real save path (SaveCoreAsync) sets the state; nothing is set by reflection, and no window is shown (ShowWindows = false)
        foreach (bool isNew in new[] { true, false })
        {
            var host = new EditorWindowHost { ShowWindows = false };
            var open = host.EditAsync(Make(isNew));
            var window = host.Window!;
            string Prompt(bool onQuit) { using var en = L.Scope("en"); return ScreenshotEditorWindow.SavePromptText((bool)flag.GetValue(window)!, onQuit); }
            try
            {
                Check.Equal(isNew, (bool)flag.GetValue(window)!, isNew ? "a new capture that was never saved" : "a picture that came from a file");
                Check.Equal(isNew ? "Save this screenshot?" : "Save the changes to this screenshot?", Prompt(onQuit: false), "before any save");
                saves = false;
                var failed = window.SaveCoreAsync(finish: false);
                ScreenshotUploadTests.Pump(() => failed.IsCompleted, "the save that fails");
                Check.False(failed.GetAwaiter().GetResult(), "the save was refused");
                Check.Equal(isNew, (bool)flag.GetValue(window)!, "a save that failed does not make it a saved screenshot");
                Check.Equal(isNew ? "Save this screenshot before Clink quits?" : "Save the changes to this screenshot before Clink quits?", Prompt(onQuit: true), "after a failed save");
                saves = true;
                var saved = window.SaveCoreAsync(finish: false);
                ScreenshotUploadTests.Pump(() => saved.IsCompleted, "the save that works");
                Check.True(saved.GetAwaiter().GetResult(), "the save worked");
                Check.False((bool)flag.GetValue(window)!, "once it has been saved, it is a saved screenshot: the changes are asked about");
                Check.Equal("Save the changes to this screenshot?", Prompt(onQuit: false), "after a save");
                Check.Equal("Save the changes to this screenshot before Clink quits?", Prompt(onQuit: true), "after a save, on quit");
            }
            finally
            {
                window.CloseApproved();
                ScreenshotUploadTests.Pump(() => open.IsCompleted, "the window to close");
            }
        }
        // both questions go through the one decision
        string root = L10nSourceScan.Root() ?? throw new TestFailure("Clink.csproj was not found above " + AppContext.BaseDirectory);
        string source = File.ReadAllText(Path.Combine(root, @"UI\Screenshots\ScreenshotEditorWindow.xaml.cs"));
        Check.Contains(source, "SavePromptText(HoldsUnsavedNewCapture, onQuit: true, onRestart: forRestart)", "the quit and restart prompt");
        Check.Contains(source, "SavePromptText(HoldsUnsavedNewCapture, onQuit: false)", "the close prompt");
    }
}