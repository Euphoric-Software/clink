using System.IO;
using Clink.Playback;
using Clink.Screenshots.Sources;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>The one way an exception carries the sentence for the user (<see cref="UserText"/>): precedence, translation at display time, and every exception type of the app using it.</summary>
internal static class UserTextTests
{
    private sealed class OwnText(string text) : Exception("english message"), IUserText
    {
        public string UserText => text;
    }

    [UnitTest]
    public static void Of_prefers_the_attached_text_then_IUserText_then_the_message()
    {
        using var scope = L.Scope("en");
        // nothing attached: the exception's own message
        Check.Equal("plain", UserText.Of(new InvalidOperationException("plain")));
        // IUserText beats the message
        Check.Equal("from the interface", UserText.Of(new OwnText("from the interface")));
        // the attached text beats IUserText and the message, as a finished string ...
        var both = UserText.Attach(new OwnText("from the interface"), "attached text");
        Check.Equal("attached text", UserText.Of(both));
        // ... and as a note
        var noted = UserText.Attach(new OwnText("from the interface"), Note.Of("Clink is not running"));
        Check.Equal("Clink is not running", UserText.Of(noted));
        // Attach returns the same exception (it is used inside throw expressions)
        var ex = new IOException("m");
        Check.True(ReferenceEquals(ex, UserText.Attach(ex, "x")), "same instance");
        // one wrapper (Task.Wait) is looked through; two are not
        Check.Equal("attached text", UserText.Of(new AggregateException(both)));
        var two = new AggregateException(both, new InvalidOperationException("x"));
        Check.Equal(two.Message, UserText.Of(two));
    }

    [UnitTest]
    public static void A_note_is_translated_when_it_is_shown_and_the_log_text_stays_english()
    {
        var ex = new ClipPlayerException(Note.Of("No video stream in file"));
        Check.Equal("No video stream in file", ex.Message, "the log text is English");
        using (L.Scope("qps-ploc"))
        {
            // the exception was built before this language was chosen: the text follows the language at display time
            Check.True(PseudoLocale.IsPseudo(UserText.Of(ex)), "shown from the catalog under the pseudo-locale: " + UserText.Of(ex));
            Check.Equal("No video stream in file", ex.Message, "the Message does not change with the language");
            Check.Equal("No video stream in file", UserText.EnglishOf(ex), "English for a log line");
        }
        using (L.Scope("en")) Check.Equal("No video stream in file", UserText.Of(ex));
    }

    [UnitTest]
    public static void Every_exception_type_that_carries_a_user_text_uses_the_helper()
    {
        using var scope = L.Scope("en");
        Check.Equal("The new copy could not be started.", UserText.Of(new SelfInstallException(Note.Of("The new copy could not be started."))));
        var capture = new ScreenshotCaptureException("log text", Note.Of("No monitor was found"));
        Check.Equal("No monitor was found", capture.UserText);
        Check.Equal("log text", capture.Message);
        Check.Equal("plain", new ScreenshotCaptureException("plain").UserText, "no note: the message");
        Check.True(capture.Data.Contains(UserText.Key), "stored under the one key");
        var copium = CopiumErrors.Shown(m => new TimeoutException(m), Note.Of("Copium did not finish its answer within {seconds} s", ("seconds", 30)));
        Check.Equal("Copium did not finish its answer within 30 s", copium.Message);
        Check.Equal(copium.Message, UserText.Of(copium));
        var http = new CopiumHttpException(503, CopiumClient.ErrorNote(503, ""));
        Check.Equal("Copium is having trouble (HTTP 503), try again later", http.Message);
        using (L.Scope("qps-ploc")) Check.True(PseudoLocale.IsPseudo(UserText.Of(http)), "the HTTP refusal is translated at display time");
        Check.Equal(503, http.StatusCode);
    }
}
