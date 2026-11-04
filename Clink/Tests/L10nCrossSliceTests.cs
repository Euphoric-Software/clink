using System.IO;
using System.Reflection;
using Clink.Engine;
using Clink.Services;
using Clink.Services.Localization;

namespace Clink.Tests;

/// <summary>
/// Places where one slice's text reached another slice's display (engine errors in the controller's lines, the share-window failure text,
/// the updater's last error in the main window): each shows the user's language and logs English, and none reads English words to decide.
/// </summary>
internal static class L10nCrossSliceTests
{
    private static string Source(string relative)
    {
        string? root = L10nSourceScan.Root();
        if (root == null) return "";   // not run from a checkout: nothing to read
        return File.ReadAllText(Path.Combine(root, relative));
    }

    [UnitTest]
    public static void Engine_errors_are_shown_through_the_helper_and_kept_english_for_the_log()
    {
        var gpu = new EngineError(EngineErrorKind.GpuReset);
        var other = new EngineError(EngineErrorKind.Other, "Access is denied. (0x80070005)");
        using (L.Scope("en"))
        {
            Check.Equal("Error: The GPU was reset (graphics driver crash or TDR)", AppController.TrayStatusFor("Error", RecordingState.Error, gpu));
            Check.Equal("Error: Access is denied. (0x80070005)", AppController.TrayStatusFor("Error", RecordingState.Error, other));
            Check.Equal("Recording", AppController.TrayStatusFor("Recording", RecordingState.Recording, gpu), "only while the state is Error");
            Check.Equal("Error", AppController.TrayStatusFor("Error", RecordingState.Error, null), "no error text: the status as it is");
        }
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            Check.True(PseudoLocale.IsPseudo(AppController.TrayStatusFor("Error", RecordingState.Error, gpu)), "the tray line is from the catalog");
            var note = Note.Of("Recording failed: {error}", ("error", EngineStatusText.ErrorNote(gpu)));
            Check.True(PseudoLocale.IsPseudo(note.Shown), "the notice is from the catalog: " + note.Shown);
            Check.Equal("Recording failed: The GPU was reset (graphics driver crash or TDR)", note.English, "the log line is English");
            Check.Equal(gpu.English, EngineStatusText.ErrorNote(gpu).English);
            // the exception's own text is not ours to translate: shown as it is, in both
            Check.Equal("Access is denied. (0x80070005)", EngineStatusText.ErrorNote(other).Shown);
            Check.Equal("Recording failed: Access is denied. (0x80070005)", Note.Of("Recording failed: {error}", ("error", EngineStatusText.ErrorNote(other))).English);
            Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every key is in the English catalog");
        }
        // the display sites do not hand the engine's English string to a translated sentence any more
        string code = Source(@"Services\AppController.cs");
        Check.False(code.Contains("(\"error\", Engine.Error)"), "no engine error string in a notice");
        Check.False(code.Contains("NotifyProblem(L.T(\"Clink is not recording\"), Engine.Error)"), "no engine error string in the notification");
        Check.False(code.Contains("TrayStatusFor(status, State, Engine.Error)"), "no engine error string in the tray line");
    }

    [UnitTest]
    public static void Not_enough_free_space_is_told_by_the_exception_type_not_by_its_words()
    {
        Check.True(typeof(IOException).IsAssignableFrom(typeof(NotEnoughFreeSpaceException)), "still an IOException for every other handler");
        string recording = Source(@"Services\AppController.Recording.cs");
        if (recording.Length == 0) return;
        Check.False(recording.Contains("StartsWith(\"Not enough free\""), "no logic on the message text");
        Check.True(recording.Contains("catch (NotEnoughFreeSpaceException"), "the type picks the sentence");
        Check.True(Source(@"Engine\Engine.cs").Contains("new NotEnoughFreeSpaceException("), "the engine throws the type");
    }

    [UnitTest]
    public static void The_share_window_failure_text_follows_the_language()
    {
        using (L.Scope("en")) Check.Equal("Could not open the share window", AppController.ShareWindowFailedText);
        using (L.Scope("qps-ploc"))
        {
            L.ClearMissing();
            Check.True(PseudoLocale.IsPseudo(AppController.ShareWindowFailedText), "from the catalog, not a constant: " + AppController.ShareWindowFailedText);
            Check.Equal(Array.Empty<string>(), L.Missing.ToArray(), "every key is in the English catalog");
        }
    }

    [UnitTest]
    public static void The_updater_s_last_error_reaches_the_main_window_in_the_users_language_and_dev_mode_is_told_by_its_flag()
    {
        // the flag is required: there is no English-text fallback any more
        var method = typeof(MainWindow).GetMethod("UpdateProblemLine", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var flag = method.GetParameters()[4];
        Check.Equal("lastErrorIsDevMode", flag.Name);
        Check.False(flag.IsOptional, "required");
        Check.Equal(typeof(bool), flag.ParameterType);
        string main = Source("MainWindow.xaml.cs");
        if (main.Length > 0)
        {
            Check.False(main.Contains("Updater.DevModeNote"), "no comparison with the English dev-mode text");
            Check.False(main.Contains("(\"error\", up.LastError)"), "the manual check shows LastErrorText");
            Check.False(main.Contains("up.LastError?.TrimEnd"), "the 'found, but ...' line shows LastErrorText");
        }
    }
}
