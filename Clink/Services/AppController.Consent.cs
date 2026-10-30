using System.Windows;
using Clink.UI;

namespace Clink.Services;

// Sharing is opt-in (the "Share with Copium?" pop-up) and has a global kill switch (Settings.CloudSharingEnabled). The rules are in
// ConsentGate; this is the controller's side: the real pop-up, the notice, the token setup, and the two-step kill switch.
internal sealed partial class AppController
{
    private ConsentGate? _consent;

    /// <summary>The one door to Copium for clips and screenshots (created on first use; the screenshot service shares it).</summary>
    internal ConsentGate Consent => _consent ??= MakeConsentGate();

    private ConsentGate MakeConsentGate() => new(Settings)
    {
        Prompt = PromptConsentAsync,
        Notice = (context, title, text) =>
        {
            Say(text);
            // A screenshot action has no footer in front of it; the clip paths report through their own NotifyProblem / row.
            if (context is ConsentContext.ScreenshotUploadManual or ConsentContext.ScreenshotUploadLibrary or ConsentContext.ScreenshotUploadSetting or ConsentContext.Proactive or ConsentContext.TokenSaved)
                Toasts.ShowNotice(title, text);
        },
        OpenTokenSetup = ShowCopiumSetup,
    };

    /// <summary>
    /// Every path to Copium asks here first (clip Share, share-last, the trim editor's share, a screenshot's Upload, turning on the
    /// automatic upload, a saved token). False: cloud sharing is off (the notice has been given) or the user did not say yes.
    /// </summary>
    internal Task<bool> EnsureCopiumConsentAsync(ConsentContext context) => Consent.EnsureAsync(context);

    /// <summary>Test seam: shows the pop-up for a context and returns the answer (the default is the real window).</summary>
    internal Func<ConsentContext, Task<ConsentAnswer>>? ConsentPromptOverride { get; set; }

    private Task<ConsentAnswer> PromptConsentAsync(ConsentContext context)
    {
        if (ConsentPromptOverride is { } over) return over(context);
        if (!_ui.CheckAccess()) return _ui.InvokeAsync(() => PromptConsentAsync(context)).Task.Unwrap();
        try
        {
            bool fromScreenshot = context is ConsentContext.ScreenshotUploadManual or ConsentContext.Proactive;
            var owner = fromScreenshot ? null : ActiveOwner();
            var window = new CopiumConsentWindow(CopiumConsentWindow.Mode.Consent, ConsentGate.OffersAutoUpload(context), excludeFromCapture: fromScreenshot || owner == null);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            Hotkeys.GrantForeground();   // asked for by a hotkey / toast: the dialog must be able to come forward
            window.ShowDialog();
            return Task.FromResult(window.Answer);
        }
        catch (Exception ex)
        {
            Log.Error("consent pop-up", ex);
            return Task.FromResult(new ConsentAnswer(ConsentChoice.NotNow));
        }
    }

    /// <summary>The window of ours the user is working in (a dialog owner), or null: the main window when it is shown, never a toast.</summary>
    private static Window? ActiveOwner()
    {
        var windows = System.Windows.Application.Current?.Windows.OfType<Window>().Where(w => w.IsVisible && w is not ToastWindow) ?? Enumerable.Empty<Window>();
        return windows.FirstOrDefault(w => w.IsActive) ?? (System.Windows.Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } m ? m : null);
    }

    // ------------------------------------------------------------------ the cloud kill switch

    /// <summary>Cloud sharing was switched (the window, the tray, hidden buttons and rows follow). Raised on the UI thread.</summary>
    public event Action? CloudSharingChanged;

    /// <summary>Test seam: the confirmation that turns cloud sharing back on (true = confirmed). The default is the dialog with the warning.</summary>
    internal Func<bool> ConfirmCloudSharingOn { get; set; } = ShowCloudSharingConfirm;

    private static bool ShowCloudSharingConfirm()
    {
        try
        {
            var owner = ActiveOwner();
            var window = new CopiumConsentWindow(CopiumConsentWindow.Mode.ConfirmCloudSharing, offerAutoUpload: false, excludeFromCapture: owner == null);
            if (owner != null) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            return window.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            Log.Error("cloud sharing confirmation", ex);
            return false;
        }
    }

    /// <summary>
    /// Turns cloud sharing off at once, or on in two steps: the confirmation first (<see cref="ConfirmCloudSharingOn"/>), the setting
    /// written only after it. Returns the setting as it is afterwards, so a caller can put its toggle back when the user said Cancel.
    /// Both transitions are logged.
    /// </summary>
    public bool SetCloudSharing(bool on)
    {
        if (Settings.CloudSharingEnabled == on) return on;
        if (on && !ConfirmCloudSharingOn())
        {
            Log.Info("cloud sharing: turning it on was cancelled");
            return Settings.CloudSharingEnabled;
        }
        Settings.CloudSharingEnabled = on;
        Settings.Save();
        Log.Info("cloud sharing: turned " + (on ? "on" : "off"));
        if (!on)
        {
            // Off means off: an upload that is already going up is stopped too (a queued screenshot one refuses itself).
            foreach (var one in _uploadCancels.ToArray())
            {
                try { one.Cancel(); } catch (ObjectDisposedException) { }
            }
            _screenshots?.CancelRunningUpload();
            // Any sign-in stops, even one that is reading the account after the approval: no /api/me, no "signed in" toast or welcome. A stored
            // token is kept (unverified); an approval that still arrives is revoked, not stored.
            _signIn?.StopForCloudSharingOff();
        }
        UpdateTrayHotkey();
        CloudSharingChanged?.Invoke();
        Consent.NotifyChanged();
        RaiseStateChanged();
        return on;
    }

    /// <summary>The line that stands in for a refused share: footer, notice. Same text everywhere (hotkey, pipe, buttons that are still showing).</summary>
    internal const string CloudOffText = ConsentGate.CloudOffText;
}
