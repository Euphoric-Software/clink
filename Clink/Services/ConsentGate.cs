namespace Clink.Services;

/// <summary>What asked to share: the gate shows the same pop-up for all of them, but a screenshot context also offers "upload every screenshot".</summary>
internal enum ConsentContext
{
    /// <summary>A clip's Share button (clip list, toast).</summary>
    ClipShare,
    /// <summary>The share-last hotkey, tray item or <c>--share-last</c>.</summary>
    ShareLast,
    /// <summary>The trim editor's "Save &amp; share".</summary>
    TrimShare,
    /// <summary>The Upload / Retry button of a screenshot notification.</summary>
    ScreenshotUploadManual,
    /// <summary>The Upload button of a screenshot row in the library pane (the main window is the dialog's owner).</summary>
    ScreenshotUploadLibrary,
    /// <summary>Turning "Upload to Copium" on in the Screenshots tab.</summary>
    ScreenshotUploadSetting,
    /// <summary>A Copium token was just saved in the App tab and sharing is still not agreed to (asked once, right after the save).</summary>
    TokenSaved,
    /// <summary>Unasked: after the second saved screenshot (see <see cref="Clink.Screenshots.CopiumNudge"/>).</summary>
    Proactive,
}

internal enum ConsentChoice { Enable, NotNow, DontAskAgain }

/// <summary>The pop-up's answer; <paramref name="AutoUpload"/>: "Also upload every screenshot I take automatically" was ticked (only offered where <see cref="ConsentGate.OffersAutoUpload"/>).</summary>
internal readonly record struct ConsentAnswer(ConsentChoice Choice, bool AutoUpload = false);

/// <summary>
/// The one door to Copium (docs/screenshots-design.md, S7). Nothing is uploaded until the user has said yes once in the "Share with
/// Copium?" pop-up (<see cref="Settings.CopiumConsent"/>), and nothing at all while the kill switch is off
/// (<see cref="Settings.CloudSharingEnabled"/>). Pure of WPF: the pop-up, the notice and the token setup are seams, so the rules
/// are tested without a window.
/// </summary>
internal sealed class ConsentGate
{
    public const string CloudOffTitle = "Cloud sharing is off";
    public const string CloudOffText = "Cloud sharing is turned off — Settings → App → Cloud sharing";

    private readonly Settings _settings;
    private TaskCompletionSource<bool>? _asking;

    public ConsentGate(Settings settings) { _settings = settings; }

    /// <summary>Shows the pop-up for a context and returns the answer. The default says "Not now" (no window): the app installs the real one.</summary>
    public Func<ConsentContext, Task<ConsentAnswer>> Prompt { get; set; } = _ => Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow));
    /// <summary>Tells the user something (context, title, text): the footer, a toast. Default: log only.</summary>
    public Action<ConsentContext, string, string> Notice { get; set; } = (_, title, text) => Log.Info("consent: " + title + ": " + text);
    /// <summary>Opens the token setup (the App tab's token box) when the user said yes and there is no token yet.</summary>
    public Action OpenTokenSetup { get; set; } = () => { };

    /// <summary>The kill switch is on (sharing allowed at all).</summary>
    public bool CloudOn => _settings.CloudSharingEnabled;

    /// <summary>Automatic screenshot uploads need the setting, the user's yes and the kill switch on (a token is checked where the upload happens).</summary>
    public bool AutoUploadAllowed => _settings.ScreenshotUpload && _settings.CopiumConsent && _settings.CloudSharingEnabled;

    /// <summary>The pop-up offers the "upload every screenshot" box for a screenshot action. Not for a clip, and not when the user is turning that very setting on.</summary>
    public static bool OffersAutoUpload(ConsentContext context) => context is ConsentContext.ScreenshotUploadManual or ConsentContext.ScreenshotUploadLibrary or ConsentContext.Proactive;

    /// <summary>A clip context has its own "no token" path (the share window), so the gate does not also open the token setup.</summary>
    private static bool IsClip(ConsentContext context) => context is ConsentContext.ClipShare or ConsentContext.ShareLast or ConsentContext.TrimShare;

    /// <summary>
    /// True when the caller may go on to Copium. Cloud sharing off: the notice, false (and no pop-up, ever). Consent given already:
    /// true. Otherwise the pop-up, once per attempt (a second caller while it is up waits for the same answer): true only for Enable.
    /// A <see cref="ConsentContext.Proactive"/> ask never shows after "Don't ask again"; an explicit one still does.
    /// </summary>
    public async Task<bool> EnsureAsync(ConsentContext context)
    {
        if (!_settings.CloudSharingEnabled)
        {
            Log.Info($"consent: refused ({context}): cloud sharing is off");
            Notice(context, CloudOffTitle, CloudOffText);
            return false;
        }
        if (_settings.CopiumConsent) return true;
        if (context == ConsentContext.Proactive && _settings.CopiumConsentDontAsk) return false;
        if (_asking is { } running) return await running.Task;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asking = tcs;
        bool ok = false;
        try { ok = await AskAsync(context); }
        catch (Exception ex) { Log.Warn("consent: the pop-up failed: " + ex.Message); }
        finally { _asking = null; tcs.TrySetResult(ok); }
        return ok;
    }

    /// <summary>The consent, the don't-ask flag, the automatic-upload setting or the kill switch changed behind a window's back (the pop-up, the tray): a window showing them re-reads the settings. May be raised on any thread.</summary>
    public event Action? ConsentChanged;

    /// <summary>Raises <see cref="ConsentChanged"/> (the controller calls it after the kill switch moved).</summary>
    public void NotifyChanged()
    {
        try { ConsentChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn("consent: a listener failed: " + ex.Message); }
    }

    private async Task<bool> AskAsync(ConsentContext context)
    {
        var answer = await Prompt(context);
        switch (answer.Choice)
        {
            case ConsentChoice.Enable:
                _settings.CopiumConsent = true;
                // The automatic upload is its own choice: set exactly by the ticked box (a stale true never survives an Enable). The
                // ScreenshotUploadSetting caller turns it on itself afterwards.
                _settings.ScreenshotUpload = OffersAutoUpload(context) && answer.AutoUpload;
                Save();
                Log.Info($"consent: Copium sharing enabled ({context}{(answer.AutoUpload && OffersAutoUpload(context) ? ", automatic screenshot uploads on" : "")})");
                NotifyChanged();
                if (string.IsNullOrWhiteSpace(_settings.CopiumToken) && !IsClip(context)) OpenTokenSetup();
                return true;
            case ConsentChoice.DontAskAgain:
                _settings.CopiumConsentDontAsk = true;
                Save();
                Log.Info($"consent: declined ({context}), not asking unasked again");
                NotifyChanged();
                return false;
            default:
                Log.Info($"consent: not now ({context})");
                return false;
        }
    }

    private void Save()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Log.Warn("consent: settings not saved: " + ex.Message); }
    }
}
