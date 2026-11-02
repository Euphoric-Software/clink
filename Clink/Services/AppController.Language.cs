using System.Windows;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Services;

// The language picker's restart: a new language is applied by starting Clink again (the same path an import takes).
internal sealed partial class AppController
{
    /// <summary>What a restart for a language asks while a recording is running: shows the question, returns the answer. The unit tests replace it (a test shows no message box).</summary>
    internal Func<Window?, string, MessageBoxResult>? LanguageRestartAsk { get; set; }

    /// <summary>
    /// What a restart for a language asks of an open screenshot editor (the answer of its quit question; false = cancel). Null: the real editor
    /// host's <see cref="EditorWindowHost.ConfirmQuitAsync"/>. The unit tests replace it (a test shows no window).
    /// </summary>
    internal Func<bool, Task<bool>>? LanguageEditorAsk { get; set; }

    /// <summary>
    /// Restarts Clink to use the language saved in Settings.Language. The recorder stops and the replay buffer is cleared (it lives in
    /// memory), so while a recording is running the user is asked first (<see cref="Dialog"/>); no is "keep running", nothing changes.
    /// An open screenshot editor with unsaved edits asks its own quit question (save, discard or cancel; cancel keeps Clink running).
    /// A restart that is refused (an import or another restart is under way, or Clink is shutting down) says so. Unlike an import, the
    /// language restart leaves the clip index live, so a recording that ends, a clip or a link that finishes during the restart is still
    /// written to clips.json (like an update restart). A <c>--dev-language</c> is not carried over (the restart would otherwise look
    /// like the picker did nothing): it is dropped, and the log says so. If the new Clink cannot be started the recorder goes on, saves
    /// are thawed, the setting stays saved (it applies at the next start) and the user is told. UI thread. Returns true when the restart
    /// is under way (with the editor's question open: asked, the answer is awaited).
    /// </summary>
    internal bool RestartForLanguage(Window? owner)
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(() => RestartForLanguage(owner)); return true; }
        if (LanguageRestartRefused()) return false;
        if (IsManualRecording)
        {
            string question = L.T("A recording is running. Restarting Clink ends it and saves what was recorded, and the replay buffer is cleared.\n\nRestart Clink to change the language?");
            MessageBoxResult answer;
            if (LanguageRestartAsk != null) answer = LanguageRestartAsk(owner, question);
            else
            {
                TestGuard.CheckLaunch("a message box (restart for the language)");
                answer = Dialog.Show(owner, question, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            }
            if (answer != MessageBoxResult.Yes) return false;
            if (LanguageRestartRefused()) return false;   // the box is modal but the app is not: a tray import can have restarted meanwhile
        }

        Func<bool, Task<bool>>? askEditor = LanguageEditorAsk;
        if (askEditor == null && _screenshotEditor is { IsOpen: true } editor) askEditor = editor.ConfirmQuitAsync;
        if (askEditor == null) return RestartNowForLanguage();

        _importInProgress = true;   // held while the editor's question is up: a second restart or an import stays out
        if (_importItem != null) _importItem.Enabled = false;
        Task<bool> asked;
        try { asked = askEditor(true); }
        catch (Exception ex) { Log.Warn("language: the screenshot editor's question failed: " + ex.Message); asked = Task.FromResult(false); }
        if (asked.IsCompletedSuccessfully) return AfterEditorQuestion(asked.Result);
        AfterEditorQuestionAsync(asked);
        return true;
    }

    private async void AfterEditorQuestionAsync(Task<bool> asked)
    {
        bool go;
        try { go = await asked; }
        catch (Exception ex) { Log.Warn("language: the screenshot editor's question failed: " + ex.Message); go = false; }
        AfterEditorQuestion(go);
    }

    /// <summary>The editor's answer: cancel keeps Clink running as it is (the tray item is back); otherwise the restart goes on.</summary>
    private bool AfterEditorQuestion(bool go)
    {
        if (_disposed || _exiting) return false;
        if (!go)
        {
            Log.Info("language: the restart was cancelled at the screenshot editor's question");
            _importInProgress = false;
            if (_importItem != null) _importItem.Enabled = true;
            return false;
        }
        return RestartNowForLanguage();
    }

    /// <summary>A restart that cannot start now (an import or a restart is under way, or Clink is going away) is refused, and the user is told.</summary>
    private bool LanguageRestartRefused()
    {
        if (!_importInProgress && !IsShuttingDown) return false;
        Log.Info("language: a restart is under way or Clink is shutting down: this request is refused");
        if (!_disposed) SayT("Clink is restarting");
        return true;
    }

    private bool RestartNowForLanguage()
    {
        _importInProgress = true;   // (also what keeps a second restart or an import out while this one is under way)
        if (_importItem != null) _importItem.Enabled = false;
        bool wasRecording = Engine.IsRunning;
        string? devLanguage = DevLanguage;
        if (devLanguage != null) { DevLanguage = null; Log.Info($"language: restarting without --dev-language {devLanguage}"); }
        Settings.Save();
        StopForRestart(freezeLibrary: false);   // the clip index stays live: the recording that ends now is added to it
        if (RestartClink(out string? error)) return true;

        DevLanguage = devLanguage;   // this Clink goes on as it was
        ContinueAfterFailedRestart(wasRecording);
        var text = Note.Of("Clink could not restart itself ({error}), so the new language applies the next time Clink starts", ("error", error));
        Say(text);
        ShowBalloon("Clink", text.Shown);
        return false;
    }
}