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
    /// host's <see cref="EditorWindowHost.ConfirmQuitAsync"/>. The unit tests replace it (a test shows no window); the quit path asks it too.
    /// </summary>
    internal Func<bool, Task<bool>>? LanguageEditorAsk { get; set; }

    /// <summary>What a quit that came in during the editor's question calls (the userInitiated flag: false = forced, true = the user's own Quit). Null: <see cref="Quit"/>. The unit tests replace it (a test must not shut the app down).</summary>
    internal Action<bool>? QuitOverride { get; set; }

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
        bool askedRecording = false;
        if (IsManualRecording)
        {
            if (!AskRecordingQuestion(owner)) return false;
            askedRecording = true;
            if (LanguageRestartRefused()) return false;   // the box is modal but the app is not: a tray import can have restarted meanwhile
        }

        Func<bool, Task<bool>>? askEditor = LanguageEditorAsk;
        if (askEditor == null && _screenshotEditor is { IsOpen: true } editor) askEditor = userInitiated => editor.ConfirmQuitAsync(userInitiated, forRestart: true);
        if (askEditor == null) return RestartNowForLanguage();

        _importInProgress = true;   // held while the editor's question is up: a second restart or an import stays out
        if (_importItem != null) _importItem.Enabled = false;
        _quitAsking = true;   // and a tray Quit or an update restart must not open a second save box over this one (QuitAsync notes a forced quit in _forcedQuitPending)
        Task<bool> asked;
        try { asked = askEditor(true); }
        catch (Exception ex) { Log.Warn("language: the screenshot editor's question failed: " + ex.Message); asked = Task.FromResult(false); }
        if (asked.IsCompletedSuccessfully) return AfterEditorQuestion(asked.Result, owner, askedRecording);
        AfterEditorQuestionAsync(asked, owner, askedRecording);
        return true;
    }

    /// <summary>The recording question of a language restart (a recording is running). True = restart anyway.</summary>
    private bool AskRecordingQuestion(Window? owner)
    {
        string question = L.T("A recording is running. Restarting Clink ends it and saves what was recorded, and the replay buffer is cleared.\n\nRestart Clink to change the language?");
        MessageBoxResult answer;
        if (LanguageRestartAsk != null) answer = LanguageRestartAsk(owner, question);
        else
        {
            TestGuard.CheckLaunch("a message box (restart for the language)");
            answer = Dialog.Show(owner, question, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        }
        return answer == MessageBoxResult.Yes;
    }

    private async void AfterEditorQuestionAsync(Task<bool> asked, Window? owner, bool askedRecording)
    {
        bool go;
        try { go = await asked; }
        catch (Exception ex) { Log.Warn("language: the screenshot editor's question failed: " + ex.Message); go = false; }
        AfterEditorQuestion(go, owner, askedRecording);
    }

    /// <summary>
    /// The editor's answer: cancel keeps Clink running as it is (the tray item is back); otherwise the restart goes on. A recording that
    /// started while the editor's question was up (the recording question was not asked, or there was none to ask) is asked about now,
    /// after the editor (the editor's save or discard is not undone by a no here). A forced quit that came in meanwhile (an update
    /// restart) wins over the language restart: the quit goes on and saves a copy of what is unsaved (unless the editor was answered yes or
    /// no: then the exit does not ask or save again, <see cref="_editorResolvedForExit"/>). The user's own Quit that came in meanwhile is
    /// acted on the same way once the editor's answer is not a cancel: Clink quits instead of restarting.
    /// </summary>
    private bool AfterEditorQuestion(bool go, Window? owner, bool askedRecording)
    {
        if (_disposed || _exiting) { _quitAsking = false; _forcedQuitPending = false; _userQuitPending = false; return false; }
        bool editorGo = go;   // the editor's own answer (yes saved, no discarded): the exit path must not ask it again
        bool forced = false, userQuit = false;
        try
        {
            if (go && !_forcedQuitPending && !_userQuitPending && !askedRecording && IsManualRecording)
            {
                go = AskRecordingQuestion(owner);
                if (!go) Log.Info("language: the restart was cancelled at the recording question (asked after the screenshot editor's)");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("language: the recording question failed: " + ex.Message);
            go = false;   // not going ahead (the branch below frees the restart)
        }
        finally
        {
            forced = _forcedQuitPending;   // (read now: the box is modal but the app is not)
            userQuit = _userQuitPending;
            _quitAsking = false;
            _forcedQuitPending = false;
            _userQuitPending = false;
        }
        if (_disposed || _exiting) return false;
        if (forced || (userQuit && editorGo))
        {
            Log.Info(forced ? "language: a forced quit came in during the question: quitting instead of restarting" : "language: Quit came in during the question: quitting instead of restarting");
            _editorResolvedForExit = editorGo;   // a cancelled editor question (forced quit only) is asked again by the exit path: it saves a copy
            bool user = !forced;
            if (QuitOverride != null) QuitOverride(user); else Quit(userInitiated: user);   // _importInProgress stays set: nothing else starts on the way out
            return false;
        }
        if (!go)
        {
            Log.Info("language: the restart was cancelled at a question");
            _importInProgress = false;
            if (_importItem != null) _importItem.Enabled = true;
            return false;
        }
        _editorResolvedForExit = editorGo;   // (true here: a cancel returned above) the restart's exit must not save the discarded edits
        return RestartNowForLanguage();
    }

    /// <summary>A restart that cannot start now (an import or a restart is under way, or Clink is going away) is refused, and the user is told.</summary>
    private bool LanguageRestartRefused()
    {
        if (!_importInProgress && !_quitAsking && !IsShuttingDown) return false;   // (_quitAsking: a quit's own save question is up)
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