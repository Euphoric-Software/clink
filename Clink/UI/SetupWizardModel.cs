using Clink.Services;

namespace Clink.UI;

internal enum SetupPage { Location, Quality, Hotkey, Startup, Done }

internal enum LocationChoice { InstallForMe, Documents, ProgramFiles, KeepHere }

/// <summary>One radio of the Location page.</summary>
internal sealed record LocationOption(LocationChoice Choice, string Label, string Hint, bool Visible, bool Enabled, string? DisabledReason = null);

/// <summary>
/// What the wizard needs to know, all gathered by the caller (startup code): nothing in the model reads the machine.
/// The last three (optional) members are display text the caller formats: <see cref="InstalledVersionText"/> for
/// "Clink {v} is already installed", <see cref="CustomQualityText"/> for the saved custom quality ("1080p, 30 fps, 12 Mbps."),
/// <see cref="PlaceTooltip"/> for the full path behind <see cref="PlaceText"/>.
/// </summary>
internal sealed record SetupContext(
    FirstRunMode Mode,
    LocationClass Location,
    bool DuplicateName,
    string PlaceText,
    bool SingleFile,
    bool Elevated,
    bool Dev,
    bool DocumentsCloudSynced,
    RelocationPlan? InstallPlan,
    RelocationPlan? DocumentsPlan,
    bool TargetInstallerManaged,
    bool OfferProgramFiles,
    bool FreshStore,
    bool ClipsEnabled,
    QualityPreset Quality,
    int ReplaySeconds,
    bool RunAtStartup,
    IReadOnlyList<HotkeyAction> Hotkeys,
    string? InstalledVersionText = null,
    string? CustomQualityText = null,
    string? PlaceTooltip = null);

/// <summary>What the wizard decided. <see cref="Hotkeys"/> is null when the hotkeys were not shown (nothing to apply).</summary>
internal sealed record SetupResult(bool Skipped, QualityPreset Quality, int ReplaySeconds, IReadOnlyList<HotkeyAction>? Hotkeys, bool RunAtStartup, bool StartMenu, bool Desktop);

/// <summary>What the window does after a model call.</summary>
internal abstract record SetupStep
{
    private SetupStep() { }
    /// <summary>Show this page (also the answer to an ignored call: the page stays).</summary>
    internal sealed record ShowPage(SetupPage Page) : SetupStep;
    /// <summary>Run this plan off the UI thread, then hand the result to <see cref="SetupWizardModel.OnRelocated"/>.</summary>
    internal sealed record Relocate(RelocationPlan Plan) : SetupStep;
    internal sealed record Finish(SetupResult Result) : SetupStep;
    /// <summary>The wizard ends without a result: a copy at the new place has taken over and this process exits.</summary>
    internal sealed record Exit : SetupStep;
}

/// <summary>
/// The first-run wizard's state machine and rules, with no WPF: pages, the Location table, defaults, busy state, texts. The window
/// only shows it. Not thread-safe: UI thread only.
/// </summary>
internal sealed class SetupWizardModel
{
    private readonly SetupContext _c;
    private readonly List<SetupPage> _pages = new();
    private int _index;
    private bool _startMenuTouched;
    private bool _busy;
    private string? _status, _error;
    private RelocationKind? _running;
    private LocationChoice _choice;
    private QualityPreset _quality;
    private int _replay;
    private bool _runAtStartup, _startMenu, _desktop;

    public SetupWizardModel(SetupContext context)
    {
        _c = context;
        if (context.Mode == FirstRunMode.Full && !context.Elevated) _pages.Add(SetupPage.Location);
        if (context.ClipsEnabled) { _pages.Add(SetupPage.Quality); _pages.Add(SetupPage.Hotkey); }
        _pages.Add(SetupPage.Startup);
        _pages.Add(SetupPage.Done);

        LocationOptions = BuildOptions(context);
        _choice = _pages[0] == SetupPage.Location && LocationOptions[0] is { Visible: true, Enabled: true } ? LocationChoice.InstallForMe : LocationChoice.KeepHere;
        _quality = context.Quality;
        _replay = context.ReplaySeconds;
        _runAtStartup = context.RunAtStartup;
        _startMenu = DefaultStartMenu();
        _desktop = false;
        HotkeysCopy = context.Hotkeys.Select(Clone).ToList();
        Warning = context.Mode == FirstRunMode.Full && !context.Elevated ? SetupWizardTexts.WarningFor(context.Location, context.DuplicateName) : null;
    }

    public event Action? Changed;
    private void Raise() => Changed?.Invoke();

    public SetupContext Context => _c;
    public SetupPage Page => _pages[_index];
    public IReadOnlyList<SetupPage> Pages => _pages;

    /// <summary>1-based number of the current page among the pages before Done; Done shows the last number.</summary>
    public int StepNumber => Math.Min(_index + 1, StepCount);
    public int StepCount => _pages.Count - 1;

    public IReadOnlyList<LocationOption> LocationOptions { get; }
    public string? Warning { get; }

    public LocationChoice Choice
    {
        get => _choice;
        set
        {
            if (_busy || value == _choice) return;
            var o = LocationOptions.First(x => x.Choice == value);
            if (!o.Visible || !o.Enabled) return;
            _choice = value;
            _error = null;
            Raise();
        }
    }

    public bool Busy => _busy;
    public string? Status => _status;
    public string? Error => _error;

    public QualityPreset Quality
    {
        get => _quality;
        set
        {
            if (_busy || value == _quality) return;
            if (value == QualityPreset.Custom && _c.Quality != QualityPreset.Custom) return;   // Custom is "keep what is saved", only on offer when something custom is saved
            _quality = value;
            Raise();
        }
    }

    public int ReplaySeconds
    {
        get => _replay;
        set
        {
            if (_busy || value == _replay) return;
            if (!SetupPresets.IsReplayChoice(value) && value != _c.ReplaySeconds) return;
            _replay = value;
            Raise();
        }
    }

    public bool RunAtStartup { get => _runAtStartup; set { if (_runAtStartup == value) return; _runAtStartup = value; Raise(); } }
    public bool StartMenu { get => _startMenu; set { _startMenuTouched = true; if (_startMenu == value) return; _startMenu = value; Raise(); } }
    public bool Desktop { get => _desktop; set { if (_desktop == value) return; _desktop = value; Raise(); } }
    public bool ShortcutsAvailable => !_c.Elevated;

    /// <summary>The hotkeys the Hotkey page edits: clones, so cancelling or skipping leaves the settings untouched.</summary>
    public IReadOnlyList<HotkeyAction> HotkeysCopy { get; private set; }

    /// <summary>The hotkey editor edits the clones in place and may add a main hotkey of its own: the window hands its list back after every change.</summary>
    internal void SetHotkeys(IReadOnlyList<HotkeyAction> list) => HotkeysCopy = list;

    /// <summary>The replay-length radios: the choices, plus the saved length when it is none of them ("Keep 80 s").</summary>
    public IReadOnlyList<int> ReplayOptions => SetupPresets.IsReplayChoice(_c.ReplaySeconds) || _c.ReplaySeconds <= 0
        ? SetupPresets.ReplayChoices
        : SetupPresets.ReplayChoices.Append(_c.ReplaySeconds).ToList();

    /// <summary>"Keep my settings" is offered: the saved quality matches no preset.</summary>
    public bool OffersCustomQuality => _c.Quality == QualityPreset.Custom;

    // ------------------------------------------------------------------ buttons

    public bool CanBack => !_busy && _index > 0;
    public bool CanNext => !_busy;
    public bool CanSkip => !_busy && Page != SetupPage.Done;

    public string NextLabel
    {
        get
        {
            if (Page == SetupPage.Done) return SetupWizardTexts.Finish;
            if (Page != SetupPage.Location) return SetupWizardTexts.Next;
            if (_error != null) return SetupWizardTexts.TryAgain;
            return _choice switch
            {
                LocationChoice.KeepHere => SetupWizardTexts.Next,
                _ => PlanFor(_choice)?.Kind == RelocationKind.LaunchExisting ? SetupWizardTexts.OpenClink : SetupWizardTexts.Install,
            };
        }
    }

    private RelocationPlan? PlanFor(LocationChoice choice) => choice switch
    {
        LocationChoice.InstallForMe => _c.InstallPlan,
        LocationChoice.Documents => _c.DocumentsPlan,
        _ => null,
    };

    public SetupStep Next()
    {
        if (_busy) return Stay();
        if (Page == SetupPage.Done) return new SetupStep.Finish(ResultFor(skipped: false));
        if (Page == SetupPage.Location && _choice != LocationChoice.KeepHere)
        {
            var plan = PlanFor(_choice);
            var option = LocationOptions.First(o => o.Choice == _choice);
            if (plan == null || !option.Visible || !option.Enabled) return Stay();
            _busy = true;
            _running = plan.Kind;
            _error = null;
            _status = plan.Kind == RelocationKind.LaunchExisting ? SetupWizardTexts.BusyStartingInstalled : SetupWizardTexts.BusyInstalling;
            Raise();
            return new SetupStep.Relocate(plan);
        }
        return Go(_index + 1);
    }

    public SetupStep Back()
    {
        if (!CanBack) return Stay();
        return Go(_index - 1);
    }

    /// <summary>Ends the wizard without applying the choices made (the settings keep their values, or get the defaults on a fresh store). Never moves anything.</summary>
    public SetupStep Skip()
    {
        if (!CanSkip) return Stay();
        return new SetupStep.Finish(ResultFor(skipped: true));
    }

    /// <summary>The window ran the plan and reports. Moved / LaunchedExisting end this process; NothingToDo carries on; a failure stays on the Location page.</summary>
    public SetupStep OnRelocated(RelocationResult result)
    {
        if (!_busy) return Stay();
        _status = null;
        switch (result.Outcome)
        {
            case RelocationOutcome.Moved:
            case RelocationOutcome.LaunchedExisting:
                return new SetupStep.Exit();   // stays Busy: nothing may be clicked while the process leaves
            case RelocationOutcome.NothingToDo:
                _busy = false;
                _error = null;
                return Go(_index + 1);
            default:   // Failed, Refused
                _busy = false;
                string why = string.IsNullOrWhiteSpace(result.Error) ? "unknown error" : result.Error;
                _error = _running == RelocationKind.LaunchExisting ? SetupWizardTexts.LaunchFailed(why) : SetupWizardTexts.InstallFailed(why);
                Raise();
                return Stay();
        }
    }

    private SetupStep Stay() => new SetupStep.ShowPage(Page);

    private SetupStep Go(int index)
    {
        _index = Math.Clamp(index, 0, _pages.Count - 1);
        if (Page == SetupPage.Startup && !_startMenuTouched) _startMenu = DefaultStartMenu();
        Raise();
        return new SetupStep.ShowPage(Page);
    }

    /// <summary>Start menu on, except for a copy that stays in a place that goes away (temporary, removable, network).</summary>
    private bool DefaultStartMenu()
    {
        if (_choice != LocationChoice.KeepHere) return true;
        return _c.Location is not (LocationClass.Temp or LocationClass.Removable or LocationClass.Network);
    }

    /// <summary>The result as it stands: <paramref name="skipped"/> = the choices made are not applied.</summary>
    public SetupResult ResultFor(bool skipped)
    {
        if (skipped)
            return new SetupResult(true, _c.Quality, _c.ReplaySeconds, null, _c.RunAtStartup, false, false);
        bool clips = _c.ClipsEnabled;
        bool shortcuts = ShortcutsAvailable;
        return new SetupResult(false, _quality, _replay, clips ? HotkeysCopy.Select(Clone).ToList() : null, _runAtStartup, shortcuts && _startMenu, shortcuts && _desktop);
    }

    /// <summary>The main hotkey as text, or null when it is not set (the Done page).</summary>
    public string? MainHotkeyText => HotkeysCopy.Count > 0 && HotkeysCopy[0].Keys is { Vk: > 0 } k ? k.ToString() : null;

    // ------------------------------------------------------------------ location table

    internal static IReadOnlyList<LocationOption> BuildOptions(SetupContext c)
    {
        var install = BuildInstall(c);
        var docs = BuildDocuments(c);
        string keepLabel = c.DocumentsPlan?.Kind == RelocationKind.NoOp ? SetupWizardTexts.KeepInDocumentsLabel : SetupWizardTexts.KeepLabel;
        var keep = new LocationOption(LocationChoice.KeepHere, keepLabel, SetupWizardTexts.KeepHint, true, true);
        var pf = new LocationOption(LocationChoice.ProgramFiles, SetupWizardTexts.ProgramFilesLabel, SetupWizardTexts.ProgramFilesHint,
            Visible: c.OfferProgramFiles && c.SingleFile && !c.Elevated, Enabled: false, DisabledReason: SetupWizardTexts.ProgramFilesUnavailable);   // slice 3: the installer branch is not wired yet
        return new[] { install, docs, pf, keep };
    }

    private static LocationOption BuildInstall(SetupContext c)
    {
        if (!c.SingleFile)
            return new(LocationChoice.InstallForMe, SetupWizardTexts.InstallForMeLabel, SetupWizardTexts.InstallForMeHint, true, false, SetupWizardTexts.CannotInstallSelf);
        var plan = c.InstallPlan;
        if (plan == null)   // no install plan (nothing to offer): hidden, as for OptionsOnly
            return new(LocationChoice.InstallForMe, SetupWizardTexts.InstallForMeLabel, SetupWizardTexts.InstallForMeHint, false, false);
        if (plan.Kind == RelocationKind.Refused)
            return new(LocationChoice.InstallForMe, SetupWizardTexts.InstallForMeLabel, SetupWizardTexts.InstallForMeHint, true, false, string.IsNullOrWhiteSpace(plan.Reason) ? SetupWizardTexts.CannotInstallSelf : plan.Reason);
        if (plan.Kind == RelocationKind.NoOp)
            return new(LocationChoice.InstallForMe, SetupWizardTexts.InstallForMeLabel, SetupWizardTexts.InstallForMeHint, false, false);
        if (plan.Kind == RelocationKind.LaunchExisting)
        {
            string hint = string.IsNullOrWhiteSpace(c.InstalledVersionText) ? SetupWizardTexts.UseInstalledHintNoVersion : SetupWizardTexts.UseInstalledHint(c.InstalledVersionText);
            return new(LocationChoice.InstallForMe, SetupWizardTexts.UseInstalledLabel, hint, true, true);
        }
        // The target holds an installer's copy but the plan is a plain install (it would overwrite it): not ours to touch.
        if (c.TargetInstallerManaged)
            return new(LocationChoice.InstallForMe, SetupWizardTexts.InstallForMeLabel, SetupWizardTexts.InstallForMeHint, true, false, SetupWizardTexts.InstalledByInstaller);
        return new(LocationChoice.InstallForMe, SetupWizardTexts.InstallForMeLabel, SetupWizardTexts.InstallForMeHint, true, true);
    }

    private static LocationOption BuildDocuments(SetupContext c)
    {
        string label = SetupWizardTexts.DocumentsLabel, hint = SetupWizardTexts.DocumentsHint;
        if (!c.SingleFile) return new(LocationChoice.Documents, label, hint, true, false, SetupWizardTexts.CannotInstallSelf);
        if (c.DocumentsCloudSynced || c.DocumentsPlan == null) return new(LocationChoice.Documents, label, hint, false, false);
        var plan = c.DocumentsPlan;
        return plan.Kind switch
        {
            RelocationKind.NoOp => new(LocationChoice.Documents, label, hint, false, false),
            RelocationKind.Refused => new(LocationChoice.Documents, label, hint, true, false, string.IsNullOrWhiteSpace(plan.Reason) ? SetupWizardTexts.CannotInstallSelf : plan.Reason),
            _ => new(LocationChoice.Documents, label, hint, true, true),
        };
    }

    internal static HotkeyAction Clone(HotkeyAction a) => new()
    {
        Keys = a.Keys?.Clone() ?? new HotkeyBinding { Vk = 0 },
        Action = a.Action,
        Seconds = a.Seconds,
        Unknown = a.Unknown == null ? null : new Dictionary<string, System.Text.Json.JsonElement>(a.Unknown),
    };
}
