using System.IO;
using System.Runtime.InteropServices;
using Clink.Engine;
using Clink.Services;
using Clink.Services.Localization;
using Rig = Clink.Tests.ShareCopyJobTests.Rig;

namespace Clink.Tests;

/// <summary>
/// Why a smaller copy was not made, in the user's language and in English: the throw sites (ShareTranscoder, ShareProbe, ShareCache) build a
/// <see cref="Note"/>, the result carries it, and the window shows it translated while the log (<see cref="Exception.Message"/>,
/// <see cref="Note.English"/>) stays English. Nothing here reads the English words to decide what to show.
/// </summary>
internal static class L10nShareReasonTests
{
    private static void Marked(string? text, string what) =>
        Check.True(!string.IsNullOrEmpty(text) && PseudoLocale.IsPseudo(text), what + ": not from the catalog: \"" + text + "\"");

    private static void English(string? text, string what)
    {
        Check.False(PseudoLocale.IsPseudo(text ?? ""), what + ": must stay English: \"" + text + "\"");
        Check.False((text ?? "").Contains("[!!"), what + ": no pseudo frame: \"" + text + "\"");
    }

    private static void NoMissing(string what) => Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), what + ": every key is in the English catalog");

    // ------------------------------------------------------------------ the reasons of the throw sites

    [UnitTest]
    public static void Every_classified_failure_has_an_english_message_and_a_reason_shown_in_the_users_language()
    {
        var cases = new (Exception Cause, string English, bool Catalog)[]
        {
            (new IOException("full", unchecked((int)0x80070070)), "the disk is full", true),
            (new COMException("no decoder", unchecked((int)0xC00D5212)), "Windows has no decoder for this clip's video", true),
            (new COMException("bad file", unchecked((int)0xC00D36C4)), "the file is not a video Clink can read", true),
            (new COMException("gpu", unchecked((int)0x887A0005)), "the graphics driver was reset", true),
            (new TimeoutException("slow"), "the hardware encoder stopped responding", true),
            (new FileNotFoundException("gone"), "the clip file is not there", true),
            (new UnauthorizedAccessException("denied"), "the copy could not be written (access denied)", true),
            (new IOException("io"), "the copy could not be written", true),
            (new InvalidOperationException("odd"), "something went wrong while encoding", true),
            // Clink's own wording around the hex code: a catalog entry, English identical to before
            (new COMException("hr", unchecked((int)0x80004005)), "Media Foundation failed (0x80004005)", true),
        };
        foreach (string language in new[] { "en", "qps-ploc" })
            using (L.Scope(language))
            {
                L.ClearMissing();
                foreach (var (cause, english, catalog) in cases)
                {
                    var typed = ShareTranscoder.Classify(cause);
                    string what = language + ": " + cause.GetType().Name;
                    Check.Equal(english, typed.Message, what + ": the Message (the log's text) is English");
                    Check.Equal(english, typed.Reason.English, what + ": the note's English");
                    string shown = UserText.Of(typed);
                    if (language == "en" || !catalog) Check.Equal(english, shown, what + ": shown");
                    else Marked(shown, what);
                    English(typed.Message, what);
                }
                var loss = ShareTranscoder.JudgeEncoderLoss("enc", 100, 100, 100, 90, 0, 0, out _, out _)!;
                Check.Equal("the hardware encoder lost frames", loss.Message);
                if (language != "en") Marked(UserText.Of(loss), "encoder loss");
                NoMissing("the classified failures (" + language + ")");
            }
    }

    [UnitTest]
    public static void A_failed_copy_is_shown_translated_while_the_result_and_the_log_stay_english()
    {
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Collect(string line) => logged.Enqueue(line);
        Log.Line += Collect;
        try
        {
            using var scope = L.Scope("qps-ploc");
            L.ClearMissing();
            using var rig = new Rig();
            var clip = rig.Clip("broken.mp4", 6_000_000);
            rig.Fake.Hold = (_, _) => throw new ShareTranscodeException(ShareTranscodeFailure.Failed, Note.Of("reason|the hardware encoder stopped responding"));
            using var r = rig.Ask(clip).GetAwaiter().GetResult();
            Check.Equal(ShareCopyOutcome.Failed, r.Outcome);
            Check.Equal("the hardware encoder stopped responding", r.Error, "the result keeps the English data");
            Check.NotNull(r.ErrorNote, "and the note");
            English(r.Error, "result.Error");

            var note = ShareFitModel.TextNote(ShareFitModel.StateFor(r), ShareFitModel.ForPlan("free"), 6_000_000, 5)!;
            Marked(note.Shown, "the window text");
            Check.Equal("Couldn't make a smaller copy: the hardware encoder stopped responding. The original is 6 MB, over Discord free (20 MB).", note.English,
                "the note's English is fully English even though the target was built in the pseudo-locale");
            English(note.English, "note.English");

            Check.True(logged.Any(l => l.Contains("share copy of broken.mp4 failed (Failed): the hardware encoder stopped responding")), "the log has the English reason");
            Check.False(logged.Any(l => l.Contains("share copy of") && l.Contains("[!!")), "no pseudo text in the log");
            NoMissing("the failed copy");
        }
        finally { Log.Line -= Collect; }
    }

    [UnitTest]
    public static void The_job_s_own_failures_carry_catalog_reasons_by_kind()
    {
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            using var rig = new Rig();
            // a clip that is not there
            var gone = new ClipEntry { Path = Path.Combine(rig.Dir.File("clips"), "never.mp4"), Size = 6_000_000, Duration = 5 };
            using var r = rig.Ask(gone).GetAwaiter().GetResult();
            Check.Equal(ShareCopyError.FileMissing, r.ErrorKind);
            Check.Equal("the clip's file is not there any more", r.Error);
            var state = ShareFitModel.StateFor(r);
            Marked(ShareFitModel.Text(state, ShareFitModel.ForPlan("free"), 6_000_000, 5), "file missing");
            // low disk: sized by the limit, chosen by the kind
            var low = ShareFitModel.StateFor(new ShareCopyResult(ShareCopyOutcome.Failed, "x.mp4", 1)
            { ErrorKind = ShareCopyError.LowDisk, Error = ShareFitModel.LowDiskReason(20_000_000), ErrorNote = ShareFitModel.LowDiskNote(20_000_000) });
            var lowNote = ShareFitModel.TextNote(low, ShareFitModel.ForPlan("free"), 46_000_000, 45)!;
            Marked(lowNote.Shown, "low disk");
            Check.Contains(lowNote.English, "there is not enough free disk space (about 111 MB are needed)");
            English(lowNote.English, "low disk English");
            // stayed too big: by the kind, never by the words
            var big = ShareFitModel.StateFor(new ShareCopyResult(ShareCopyOutcome.Failed, "x.mp4", 1) { ErrorKind = ShareCopyError.StayedTooBig, Error = "words that mean nothing" });
            Check.Contains(ShareFitModel.TextNote(big, ShareFitModel.ForPlan("free"), 46_000_000, 45)!.English, "the copy stayed too big");
            // a reason that only arrives as text is shown as it is
            var plain = ShareFitModel.StateFor(new ShareCopyResult(ShareCopyOutcome.Failed, "x.mp4", 1) { ErrorKind = ShareCopyError.Failed, Error = "the disk is full" });
            Check.Contains(ShareFitModel.TextNote(plain, ShareFitModel.ForPlan("free"), 46_000_000, 45)!.English, ": the disk is full.");
            NoMissing("the job's failures");
        }
    }

    // ------------------------------------------------------------------ why smaller copies are not available

    [UnitTest]
    public static void The_unavailable_reason_is_an_enum_with_one_whole_sentence_each()
    {
        var free = ShareFitModel.ForPlan("free");
        string tail = " The original is 46 MB, over Discord free (20 MB).";
        var expected = new (ShareUnavailableKind Why, string English)[]
        {
            (ShareUnavailableKind.Elevated, "Smaller copies are not available while Clink runs as administrator." + tail),
            (ShareUnavailableKind.Redirected, "Smaller copies are not available because the share cache folder is redirected somewhere else (a junction or link)." + tail),
            (ShareUnavailableKind.Linked, "Smaller copies are not available because the share cache folder for this copy is a junction or link." + tail),
            (ShareUnavailableKind.Unknown, "Smaller copies are not available." + tail),
        };
        foreach (string language in new[] { "en", "qps-ploc" })
            using (L.Scope(language))
            {
                L.ClearMissing();
                foreach (var (why, english) in expected)
                {
                    var note = ShareFitModel.UnavailableNote(free, why, 46_000_000);
                    Check.Equal(english, note.English, why + ": English");
                    if (language == "en") Check.Equal(english, note.Shown, why + ": shown");
                    else Marked(note.Shown, why.ToString());
                    // no English sentence is spliced into another: the whole text is one catalog entry
                    Check.False(note.Key.Contains("{reason}"), why + ": no {reason} slot");
                }
                NoMissing("the unavailable sentences (" + language + ")");
            }

        // the exception, the result and the state carry the enum, not words
        var ex = new ShareCacheUnavailableException(ShareCache.RedirectedReason, ShareUnavailableKind.Redirected);
        Check.Equal(ShareUnavailableKind.Redirected, ex.Why);
        var result = new ShareCopyResult(ShareCopyOutcome.UseOriginal, "a.mp4", 46_000_000) { ErrorKind = ShareCopyError.CacheUnavailable, Error = ex.Message, Unavailable = ex.Why };
        var state = ShareFitModel.StateFor(result);
        Check.Equal(ShareFitStateKind.Unavailable, state.Kind);
        Check.Equal(ShareUnavailableKind.Redirected, state.Why);
        using (L.Scope("qps-ploc")) Marked(ShareFitModel.Text(state, free, 46_000_000, 45), "unavailable state");
        // a result with only words (nobody said why) gets the sentence that says nothing more than it is so
        var wordsOnly = ShareFitModel.StateFor(result with { Unavailable = ShareUnavailableKind.Unknown, Error = "some words" });
        Check.Equal("Smaller copies are not available." + tail, ShareFitModel.Text(wordsOnly, free, 46_000_000, 45));
    }

    [UnitTest]
    public static void The_job_says_why_the_cache_is_unavailable_as_an_enum()
    {
        using var rig = new Rig(elevated: () => true);
        var clip = rig.Clip("admin.mp4", 6_000_000);
        using var r = rig.Ask(clip).GetAwaiter().GetResult();
        Check.Equal(ShareCopyOutcome.UseOriginal, r.Outcome);
        Check.Equal(ShareUnavailableKind.Elevated, r.Unavailable);
        Check.Equal(ShareCache.ElevatedReason, r.Error, "English data");
        var cache = new ShareCache(rig.Dir.File("cache2"), isElevated: () => true);
        var thrown = Check.Throws<ShareCacheUnavailableException>(() => cache.BeginCopy(new string('1', 32), "a.mp4"));
        Check.Equal(ShareUnavailableKind.Elevated, thrown.Why);
        Check.Equal(ShareCache.ElevatedReason, thrown.Message);
        cache.Dispose();
    }

    // ------------------------------------------------------------------ the plan name keeps English logs English

    [UnitTest]
    public static void A_target_built_in_another_language_keeps_the_english_of_its_notes_english()
    {
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            var free = ShareFitModel.ForPlan("free");
            var copium = ShareFitModel.ForCopium(true);
            Marked(free.Label, "the label itself is translated (the combo and the buttons)");
            Check.Equal("Discord free (20 MB)", free.NameKey, "the key rides on the target");
            Check.Equal("Copium Plus", copium.ShortLabel);
            var notes = new[]
            {
                (ShareFitModel.OverNote(free, 46_000_000), "Too big for Discord free (20 MB): this clip is 46 MB"),
                (ShareFitModel.FailedNote(free, "the disk is full", 46_000_000), "Couldn't make a smaller copy: the disk is full. The original is 46 MB, over Discord free (20 MB)."),
                (ShareFitModel.StayedTooBigNote(free, 46_000_000, 30), "Couldn't make a smaller copy: the copy stayed too big. The original is 46 MB, over Discord free (20 MB). Trim it to about 0:30."),
                (ShareFitModel.UnavailableNote(free, ShareUnavailableKind.Elevated, 46_000_000), "Smaller copies are not available while Clink runs as administrator. The original is 46 MB, over Discord free (20 MB)."),
                (ShareFitModel.GameRefusalNote(free), "Too big for Discord free (20 MB): open Clink to make a smaller copy"),
                (ShareFitModel.GameRefusalNote(copium), "Too big for Copium Plus: open Clink to make a smaller copy"),
                (ShareFitModel.GameRefusalNote(ShareFitModel.ForPlan("nitro")), "Too big for Discord Nitro (1 GB): open Clink to make a smaller copy"),
            };
            foreach (var (note, english) in notes)
            {
                Check.Equal(english, note.English, "English: " + english);
                English(note.English, english);
                Marked(note.Shown, "shown: " + english);
            }
            // the product names of a service are never translated, even inside the pseudo-locale's frame
            Check.Contains(ShareFitModel.GameRefusalNote(copium).Shown, "Copium Plus");
            NoMissing("the target notes");
        }
        // a target with a plain label (no key) is shown as it is
        var plain = new ShareFitTarget(2_000_000, 0, "Discord test (2 MB)");
        Check.Equal("Too big for Discord test (2 MB): this clip is 6 MB", ShareFitModel.OverNote(plain, 6_000_000).English);
    }

    [UnitTest]
    public static void The_game_refusal_result_stores_the_english_text_and_the_window_shows_it_in_the_users_language()
    {
        using var rig = new Rig();
        rig.App.GameOrFullScreenProbe = () => true;
        rig.S.ShareCopiesDuringGames = false;
        var clip = rig.Clip("game.mp4", 6_000_000);
        using var scope = L.Scope("qps-ploc");
        using var r = rig.Ask(clip).GetAwaiter().GetResult();
        Check.Equal(ShareCopyOutcome.Refused, r.Outcome);
        Check.Equal("Too big for Discord test (2 MB): open Clink to make a smaller copy", r.Error, "English data, nothing translated is kept");
        English(r.Error, "refusal Error");
        Marked(ShareFitModel.Text(ShareFitModel.StateFor(r), rig.Target, 6_000_000, 5), "the refusal the window shows");
    }
}
