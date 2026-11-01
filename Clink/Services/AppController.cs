using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Clink.Audio;
using Clink.Capture;
using Clink.Engine;
using Clink.Native;
using Clink.Services.Localization;
using Clink.UI;

namespace Clink.Services;

/// <summary>
/// A sentence for the user that is kept as its catalog key plus arguments, so it can be shown in the language of the UI
/// (<see cref="Shown"/>) and written to the log in English (<see cref="English"/>). An argument may itself be a <see cref="Note"/>
/// (a reason that is a sentence of its own); it is resolved the same way, so the log never gets a translated piece.
/// </summary>
internal sealed class Note
{
    public string Key { get; }
    private readonly (string Name, object? Value)[] _args;
    private readonly long? _count;
    private readonly string? _verbatim;
    private readonly bool _trimPeriod;
    private readonly Func<string>? _compose;

    private Note(string key, (string Name, object? Value)[] args, long? count = null, string? verbatim = null, bool trimPeriod = false, Func<string>? compose = null) { Key = key; _args = (args ?? Array.Empty<(string, object?)>()).ToArray(); _count = count; _verbatim = verbatim; _trimPeriod = trimPeriod; _compose = compose; }

    /// <summary>This note without a final full stop (<see cref="Shown"/> and <see cref="English"/>), for a sentence that is used as a clause of another one.</summary>
    public Note WithoutFinalPeriod() => new(Key, _args, _count, _verbatim, trimPeriod: true, compose: _compose);

    /// <summary>
    /// A note for a text that is put together from several catalog entries (a join of sentences, a choice between entries by a count): the
    /// text of <paramref name="compose"/> is what <see cref="Shown"/> returns, and <see cref="English"/> runs it again with every catalog call
    /// answering in English (<see cref="L.InEnglish"/>), so the log never gets a translated piece. <paramref name="compose"/> must only build
    /// text (no side effects, no waiting) and take any text it was given from outside as it is.
    /// </summary>
    public static Note Build(Func<string> compose) => new("(composed)", Array.Empty<(string, object?)>(), compose: compose);

    /// <summary>A note for catalog entry <paramref name="key"/> (its English text, see <see cref="L.T"/>) with <c>{name}</c> arguments.</summary>
    public static Note Of(string key, params (string Name, object? Value)[] args) => new(key, args);

    /// <summary>A text that is shown and logged exactly as it is (an error text of the system or of a library: no catalog entry).</summary>
    public static Note Verbatim(string text) => new(text, Array.Empty<(string, object?)>(), null, text);

    /// <summary>An argument (a path, a version, a key combo) that keeps its left-to-right direction inside right-to-left text; the marks are added when the note is <see cref="Shown"/>, never in <see cref="English"/>.</summary>
    internal readonly record struct LtrArg(string Text);

    /// <summary>A byte count that is written with the units of the language it is shown in (<see cref="Shown"/>) or of English (<see cref="English"/>): the log never gets a translated unit.</summary>
    internal readonly record struct SizeArg(long Bytes);

    /// <summary>A byte count as an argument of a note (see <see cref="SizeArg"/>); use it instead of <c>Capabilities.FormatBytes</c> in <see cref="Of"/>.</summary>
    public static SizeArg Size(long bytes) => new(bytes);

    /// <summary>Marks <paramref name="text"/> as a left-to-right argument of a note (see <see cref="LtrArg"/>); use it instead of <see cref="L.Ltr"/> in <see cref="Of"/> / <see cref="PluralOf"/>.</summary>
    public static LtrArg Ltr(string text) => new(text);

    /// <summary>An argument of unknown direction (a monitor name, a size) that keeps its own direction inside right-to-left text; the marks are added when the note is <see cref="Shown"/> (<see cref="L.Isolate"/>), never in <see cref="English"/>.</summary>
    internal readonly record struct IsolateArg(string Text);

    /// <summary>Marks <paramref name="text"/> as an argument that keeps its own direction (see <see cref="IsolateArg"/>).</summary>
    public static IsolateArg Isolate(string text) => new(text);

    private static readonly System.Text.RegularExpressions.Regex s_marks = new("[\u202A-\u202E\u200E\u200F\u2066-\u2069]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A note for a plural catalog entry (<see cref="L.Plural"/>; <c>{count}</c> is filled in). Only for counts English writes in its "other"
    /// form (not 1): the English log line is that form.
    /// </summary>
    public static Note PluralOf(string key, long count, params (string Name, object? Value)[] args) => new(key, args, count);

    private (string Name, object? Value)[] Resolve(bool english) =>
        _args.Select(a => (a.Name, a.Value switch
        {
            Note n => (object)(english ? n.English : n.Shown),
            LtrArg l => english ? l.Text : L.Ltr(l.Text),
            IsolateArg i => english ? i.Text : L.Isolate(i.Text),
            SizeArg b => english ? L.BytesIn("en", b.Bytes) : L.Bytes(b.Bytes),
            _ => a.Value,
        })).ToArray();

    /// <summary>The text in the language of the UI.</summary>
    public string Shown => Trim(_verbatim ?? (_compose != null ? _compose() : _count is long c ? L.Plural(Key, c, Resolve(false)) : L.T(Key, Resolve(false))));

    private string Trim(string s) => _trimPeriod ? s.TrimEnd('.', '\u3002', '\uFF0E', '\u0964') : s;

    /// <summary>The text in English (logs), whatever the language of the UI is; free of direction marks.</summary>
    public string English => Trim(_verbatim ?? (_compose != null ? s_marks.Replace(L.InEnglish(_compose), "") : s_marks.Replace(L.TIn("en", Key, _count is long c ? Resolve(true).Append(("count", (object?)c)).ToArray() : Resolve(true)), "")));

    /// <summary>English, so a note that ends up in a log line by string concatenation stays English; use <see cref="Shown"/> for the user.</summary>
    public override string ToString() => English;
}

/// <summary>What Clink is doing right now, for the window, the tray and the status dot.</summary>
internal enum RecordingState { Starting, Recording, WaitingForGame, Paused, Error }

/// <summary>
/// How a Share ended (<see cref="AppController.LastShareOutcome"/>): a link copied or uploaded; the share window opened because there is
/// no Copium token (nothing failed); turned down for Copium's limits; failed (the upload, or no window over the game); or, rarely,
/// the share window itself could not be opened (<see cref="AppController.ShareError"/> says so; no upload was tried).
/// </summary>
internal enum ShareOutcome { None, LinkCopied, Uploaded, FileShareOpened, Refused, Failed, WindowFailed, Declined }

/// <summary>
/// Why a Share was refused (<see cref="AppController.RefusalKind"/>): over Copium's limits for the account, no smaller copy could be made
/// (the refusal is a whole sentence of its own), or a refusal that is a phrase rather than a sentence (a game is in front).
/// </summary>
internal enum ShareRefusalKind { None, OverLimit, SmallerCopy, Phrase }

/// <summary>Owns the engine, settings, clip library, hotkeys, game detection, toasts, status dot, archive, tray icon and the single-instance pipe.</summary>
internal sealed partial class AppController : IDisposable
{
    /// <summary>
    /// Appended to the mutex and pipe names so a "--dev" instance runs next to the real one without ever seeing its commands:
    /// "" for a normal run, ".dev.&lt;hash of the data folder&gt;" with <c>--dev</c> (<see cref="DevOptions.InstanceSuffix"/>). Set before
    /// <see cref="AcquireSingleInstance"/>, never changed afterwards.
    /// </summary>
    public static string InstanceSuffix { get; set; } = "";
    /// <summary>Running as <c>--dev &lt;folder&gt;</c>: own data folder, mutex and pipe; no updater, no Run key, hotkeys off unless <see cref="DevHotkeys"/>.</summary>
    public static bool IsDev => InstanceSuffix.Length > 0;
    /// <summary><c>--dev-hotkeys</c>: a dev instance binds its hotkeys too (its hook then swallows those combos system-wide while it runs).</summary>
    public static bool DevHotkeys { get; set; }
    /// <summary>Hidden <c>--dev-accept-import</c> (test aid): a dev instance imports the other copy's settings the moment one is offered, as if the tray item had been clicked and confirmed.</summary>
    public static bool DevAcceptImport { get; set; }
    /// <summary><c>--dev-token</c>: a dev instance keeps the Copium token its settings hold (otherwise it has none).</summary>
    public static bool DevToken { get; set; }
    /// <summary><c>--dev-record</c>: a dev instance records. Without it, it starts paused: recording next to the user's game is not what a test run is for.</summary>
    public static bool DevRecord { get; set; }
    /// <summary>A dev instance that was not asked to record (<c>--dev-record</c>) starts paused.</summary>
    internal static bool StartsPaused => QuietMode.StartsPaused(IsDev, DevRecord, DevQuiet, DevQuietRecord);
    /// <summary><c>--dev-quiet</c>: a dev instance that stays out of the user's way (<see cref="QuietMode"/>). Only meaningful with <c>--dev</c> (<see cref="IsQuiet"/>).</summary>
    public static bool DevQuiet { get; set; }
    /// <summary><c>--dev-quiet-record</c>: a quiet instance records (otherwise it starts paused).</summary>
    public static bool DevQuietRecord { get; set; }
    /// <summary>A dev instance started with <c>--dev-quiet</c>; false for a normal run and for a plain <c>--dev</c> run.</summary>
    public static bool IsQuiet => QuietMode.IsQuiet(IsDev, DevQuiet);
    /// <summary><c>--dev-no-marker</c> (quiet dev instances only): documentation screenshots without the TEST marker.</summary>
    public static bool DevNoMarker { get; set; }
    /// <summary><c>--dev-demo-status "game"</c> (quiet dev instances only): the game the header pretends to record.</summary>
    public static string? DevDemoStatus { get; set; }
    /// <summary>Hidden <c>--dev-language &lt;code&gt;</c> (dev instances only): the UI language to look at by hand, whatever the settings say (see <see cref="DevOptions.Language"/>).</summary>
    public static string? DevLanguage { get; set; }
    /// <summary>The TEST frame, badge and tags are drawn: a dev instance, unless <c>--dev-no-marker</c> hides them.</summary>
    public static bool ShowsDevMarker => IsDev && !DocShots;
    /// <summary>Documentation-screenshot mode: <c>--dev</c> with <c>--dev-quiet</c> and <c>--dev-no-marker</c> (nothing else turns it on).</summary>
    public static bool DocShots => QuietMode.HidesMarker(IsDev, DevQuiet, DevNoMarker);
    /// <summary>"Recording · game" while <c>--dev-demo-status</c> is in force, otherwise null.</summary>
    public static string? DemoStatusText => QuietMode.DemoStatusText(IsDev, DevQuiet, DevDemoStatus);
    /// <summary>The game <c>--dev-demo-status</c> pretends to have detected, otherwise null.</summary>
    public static string? DemoGame => QuietMode.DemoGameName(IsDev, DevQuiet, DevDemoStatus);
    public static string PipeName => "Clink.Control" + InstanceSuffix;
    /// <summary>
    /// The pipe both ends really use: <see cref="PipeName"/> plus this Windows session's id, so a Clink in another session (another
    /// signed-in user, a remote session) never receives this one's commands. The server and the client are always in one session.
    /// </summary>
    public static string SessionPipeName => PipeName + ".s" + SessionId.ToString(CultureInfo.InvariantCulture);
    private static int SessionId { get { try { return Process.GetCurrentProcess().SessionId; } catch (Exception) { return 0; } } }
    internal static string MutexName => "Clink.SingleInstance.6F1B" + InstanceSuffix;

    /// <summary>The switches that make a restarted process this same dev instance (see <see cref="DevOptions.Switches"/>); none for a normal run.</summary>
    internal static List<string> DevSwitches() =>
        IsDev ? DevOptions.Switches(Settings.Dir, DevHotkeys, DevRecord, DevToken, SettingsImport.PackagesRoot, DevQuiet, DevQuietRecord, DevNoMarker, DevDemoStatus, DevLanguage) : new List<string>();
    private static readonly TimeSpan GameExitGrace = TimeSpan.FromSeconds(5);   // keep recording this long after the game's process is gone

    public static AppController Instance { get; private set; } = null!;

    public Settings Settings { get; }
    public ClipLibrary Library { get; } = new();
    public Engine.Engine Engine { get; } = new();
    public Updater Updater { get; }
    public Toasts Toasts { get; }
    public ArchiveService Archive { get; }
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public bool IsPaused { get; private set; }
    public string LastNotice { get; private set; } = "";
    /// <summary>Why the last <see cref="ShareAsync"/> refused to upload (Copium's free limits), for the clip row and the toast to show instead of "Upload failed"; null when it was not refused.</summary>
    public string? ShareRefusal => _refusal?.Shown;
    private Note? _refusal;
    /// <summary>What kind of refusal <see cref="ShareRefusal"/> is, so a caller words its sentence by the kind instead of editing the refusal text (None when it was not refused).</summary>
    internal ShareRefusalKind RefusalKind { get; private set; }
    /// <summary>Why the last <see cref="ShareAsync"/> failed (no token, Copium's answer, the network), for the clip row, the toast and the trim editor to show; null when it did not fail.</summary>
    public string? ShareError => _shareError?.Shown;
    private Note? _shareError;
    /// <summary><see cref="ShareError"/> as a note (English for a log).</summary>
    internal Note? ShareErrorNote => _shareError;
    /// <summary>The last upload failed because Copium rejected the token (HTTP 401 / 403): a bulk upload stops instead of trying the rest.</summary>
    internal bool ShareTokenRejected { get; private set; }
    /// <summary>
    /// How the last <see cref="ShareAsync"/> ended, set on every path of it. The callers tell "the share window opened" (no Copium
    /// token: the user has the file to drag, nothing failed, so no error line) from a failure by it; read it right after the call returns.
    /// </summary>
    public ShareOutcome LastShareOutcome { get; private set; }
    /// <summary>What <see cref="ShareError"/> says when <see cref="LastShareOutcome"/> is <see cref="ShareOutcome.WindowFailed"/>: not an upload failure, so the row and the toast show it as it is.</summary>
    public static string ShareWindowFailedText => L.T("Could not open the share window");
    /// <summary>A Copium upload token is saved: Share uploads. Without one it opens the share window (the file to drag) instead.</summary>
    public bool HasCopiumToken => !string.IsNullOrWhiteSpace(Settings.CopiumToken);
    /// <summary>Version of a downloaded update waiting to be installed, or null.</summary>
    public ReleaseVersion? PendingVersion => Updater.PendingVersion;

    /// <summary>Name of the detected game / custom app (process name), or null while none runs. Set by the 1 s detection poll in both recording modes.</summary>
    public string? CurrentGame { get; private set; }
    /// <summary>"fullscreen" | "library" | "custom": which signal found <see cref="CurrentGame"/>.</summary>
    public string? CurrentGameSource { get; private set; }
    /// <summary>Settings.RecordingMode == "game": the engine runs only while a game or custom app is running.</summary>
    public bool GameOnly => string.Equals(Settings.RecordingMode, "game", StringComparison.OrdinalIgnoreCase);

    public RecordingState State
    {
        get
        {
            if (IsPaused) return RecordingState.Paused;
            if (Engine.IsRunning) return RecordingState.Recording;
            if (GameOnly && _gamePid == 0) return RecordingState.WaitingForGame;
            if (Engine.Error != null) return RecordingState.Error;
            return RecordingState.Starting;
        }
    }

    /// <summary>A manual recording (start / stop) is going: the status dot is red and the status line counts its time. Not the replay buffer.</summary>
    public bool IsManualRecording => !IsPaused && (Engine.IsRecording || _wantRecording);

    /// <summary>One line for the UI header: "Recording · Overwatch", "Recording 00:12 · Overwatch" (manual), "Waiting for a game…", "Paused", "Error", "Starting…".</summary>
    public string StatusText => StatusTextFor(Settings.ClipsEnabled, State, _wantRecording && !Engine.IsRecording,
        IsManualRecording && Engine.Recording is { } rec ? rec.Seconds : null, CurrentGame);

    /// <summary><see cref="StatusText"/> for a given situation (so each state can be checked without a running recorder).</summary>
    internal static string StatusTextFor(bool clipsEnabled, RecordingState state, bool betweenParts, double? manualSeconds, string? game) => !clipsEnabled ? L.T("Clips are off") : state switch
    {
        RecordingState.Paused => L.T("Paused"),
        _ when betweenParts => L.T("Recording (resuming…)"),   // between two parts of a recording
        RecordingState.Recording when manualSeconds is double secs => RecordingPolicy.StatusLine(secs, game),
        RecordingState.Recording => game != null ? L.T("Recording · {game}", ("game", L.Isolate(game))) : L.T("status|Recording"),
        RecordingState.WaitingForGame => L.T("Waiting for a game…"),
        RecordingState.Error => L.T("Error"),
        _ => L.T("Starting…"),
    };

    public event Action? StateChanged;
    public event Action<string>? Notice;
    public event Action<ClipEntry>? ClipSaved;
    public event Action? ShowRequested;
    /// <summary>"Clink.exe --trim-last" (or the pipe command "trim"): open the trim editor on the newest clip.</summary>
    public event Action? TrimLastRequested;
    /// <summary>A toast's Trim button: open the trim editor on this clip. When nobody listens the toast opens the editor itself.</summary>
    public event Action<ClipEntry>? TrimRequested;
    /// <summary>
    /// An update became ready to install, the pending state changed otherwise, or a check pass ended (background passes included: its
    /// result or problem is to be shown). Raised on the UI thread.
    /// </summary>
    public event Action? UpdateStateChanged;

    private readonly Dispatcher _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _engineGate = new(1, 1);   // Engine.Start / Stop are not thread-safe against each other
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;     // the tray's icon at the taskbar's small-icon size (owned here, unlike SystemIcons)
    private System.Windows.Forms.ToolStripMenuItem? _statusItem, _clipItem, _pauseItem, _updateItem, _importItem, _screenshotItem, _screenshotEditItem, _shareLastItem, _openFolderItem;
    private DispatcherTimer? _timer;
    private StatusDotWindow? _dot;
    private static Mutex? s_mutex;
    private int _saving;
    private Task<ClipEntry?>? _saveTask;    // the save in flight (UI thread): its clip once in the library, null if it failed
    private DateTime _clipRefusedAt = DateTime.MinValue;   // a clip turned down before any save started (not recording, --clip at startup)
    // Copium uploads in flight (UI thread); Announced: started by ShareLast, which reports a failure itself.
    private readonly Dictionary<ClipEntry, (Task<ShareOutcome> Task, bool Announced)> _uploads = new(ReferenceEqualityComparer.Instance);

    /// <summary>The clip is being uploaded to Copium now (from the clip list, the share hotkey, the tray or a toast: all go through the same upload record). UI thread.</summary>
    internal bool IsUploading(ClipEntry entry) => _uploads.Keys.Any(e => ReferenceEquals(e, entry) || string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
    private readonly List<CancellationTokenSource> _uploadCancels = new();   // one per running clip upload (UI thread): the cloud kill switch cancels them
    private DateTime _lastClipAt = DateTime.MinValue;
    private int _startGen;
    private bool _starting;
    private bool _stopping;                 // a StopEngine is in flight: Engine.IsRunning reads true until its teardown has finished
    private DateTime _lastStartAt = DateTime.MinValue;
    private int _startFailures;             // engine starts that failed in a row (Tick's retry backoff, one balloon per streak)
    private bool _disposed;
    private uint _gamePid;
    private DateTime? _gameStopAt;          // game-only mode: stop the engine at this time unless a game comes back
    private string? _announceGame;          // show the "Recording started" toast for this game once the engine is up
    private int _detecting;
    private string _trayText = "";
    private string? _hotkeyNotice;          // said once at start when the main hotkey was claimed for Alt+C or left unset (MigrateHotkeys)
    private bool _importOffered, _importAccepted;   // another copy's settings were offered in a balloon (once per start); --dev-accept-import has acted
    private bool _wantRecording;            // the user started a manual recording and has not stopped it (UI thread): a technical end continues it as a new part
    private EngineStopCause _stopCause;     // why the controller stopped the engine under a recording (UI thread); consumed by OnRecordingEnded
    private bool _settingsRestartPending;   // a settings change wanted an engine restart while recording: applied when the recording ends
    private bool _exiting;                  // Quit / session end: StopRecordingForExit has run
    private DateTime _lastRecordToggleAt = DateTime.MinValue;
    private int _recordingGen;              // bumped by every start / stop request: a part-continuation loop of an older wish gives up
    private int _engineRunGen;              // bumped each time a start of the engine completes with the engine running: a part waits for a newer run than the one it ended with
    private int _partRunGen;                // _engineRunGen when the recording that is going (or ended last) started
    private DateTime _technicalRestartUntil = DateTime.MinValue; // a display change / resume is on its way to restarting the engine until this time (UI thread): no part starts on the old engine
    private DateTime _recordWhenReadyUntil = DateTime.MinValue;   // a start asked for while the recorder was not ready (or --record at launch) is acted on until this time
    private bool _autoPart;                 // the recording that is going (or ended last) was started by the controller as a continuation, not by the user
    private int _emptyAutoParts;            // automatic parts in a row that recorded nothing
    private bool _finishing;                // the user stopped the recording and its file is being finished
    private readonly object _recoverGate = new();   // one RecoverRecordings pass at a time (a folder change during the startup pass)
    private System.Windows.Forms.ToolStripMenuItem? _recordItem;
    private static readonly TimeSpan ExitStopLimit = TimeSpan.FromSeconds(4);

    public AppController(Settings settings, Updater updater)
    {
        Settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        Instance = this;
        CopiumClient.UploadTooLarge += OnUploadTooLarge;
        Updater = updater;
        Toasts = new Toasts(settings, () => SelectedMonitor);
        Archive = new ArchiveService(settings, Library);
        Updater.UpdateAvailable += info => _ui.BeginInvoke(() => OnUpdateAvailable(info));
        Updater.UpdateReady += version => _ui.BeginInvoke(() => OnUpdateReady(version));
        Updater.PendingCleared += () => _ui.BeginInvoke(() => OnPendingCleared());
        Updater.CheckFinished += notice => _ui.BeginInvoke(() => OnCheckFinished(notice));
    }

    // ------------------------------------------------------------------ single instance

    /// <summary>Returns true if this process should run; otherwise the command was forwarded to the running instance.</summary>
    public static bool AcquireSingleInstance(string[] args)
    {
        bool createdNew;
        try { s_mutex = new Mutex(true, MutexName, out createdNew); }
        catch (UnauthorizedAccessException) { createdNew = false; }   // an elevated Clink owns it: it is running all the same
        if (createdNew) return true;
        SendCommand(CommandFromArgs(args) ?? "show");
        return false;
    }

    /// <summary>This process's Windows session id (0 when it cannot be read).</summary>
    internal static int CurrentSessionId => SessionId;

    /// <summary>A Clink process found running (<see cref="RunningClinks"/>): its pid, session, exe path and version text (ProductVersion, else FileVersion); null where unreadable.</summary>
    internal readonly record struct RunningClink(int Pid, int SessionId, string? ExePath, string? Version);

    /// <summary>
    /// The message for a plain start that found the single-instance mutex taken by an OLDER Clink running from another exe (v0.4.1 or
    /// earlier cannot update itself any more: its users upgrade by hand, and the old copy in the tray would keep the new one from
    /// starting), or null. Only for a plain start: <paramref name="forwardedCommand"/> null (no --clip, --show, ...) and not
    /// <paramref name="dev"/>. Only a process in <paramref name="ownSession"/>, other than <paramref name="ownPid"/>, whose exe path is not
    /// <paramref name="ownPath"/> and whose version sorts below <paramref name="ownVersion"/> counts. Also null when this start is a
    /// <paramref name="restart"/> (--after-pid), and when ANY other process of the session runs from this very exe path or has a version
    /// at or above this one: then the instance holding the lock is probably not the old one. Pure: the process list is given.
    /// </summary>
    internal static string? OlderRunningCopy(IEnumerable<RunningClink> running, string? ownPath, ReleaseVersion? ownVersion, int ownPid, int ownSession,
        string? forwardedCommand, bool dev, bool restart = false)
    {
        if (dev || restart || forwardedCommand != null || ownVersion is not { } own || string.IsNullOrEmpty(ownPath)) return null;
        string self = FullPathOrSelf(ownPath);
        string? message = null;
        foreach (var p in running)
        {
            if (p.Pid == ownPid || p.SessionId != ownSession || string.IsNullOrEmpty(p.ExePath)) continue;
            if (string.Equals(FullPathOrSelf(p.ExePath), self, StringComparison.OrdinalIgnoreCase)) return null;   // this very exe runs already: it holds the lock
            if (ReleaseVersion.Parse(p.Version) is not { } v) continue;
            if (v >= own) return null;   // a copy as new as this one (or newer) is probably the one holding the lock
            message ??= L.T("An older Clink (v{version}) is still running from {path}. Right-click its tray icon, choose Quit, then start Clink again.", ("version", L.Ltr(v.ToString())), ("path", L.Ltr(p.ExePath)));
        }
        return message;
    }

    private static string FullPathOrSelf(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception) { return path; }
    }

    /// <summary>The version text of an exe (ProductVersion when it reads as a version, else FileVersion), or null.</summary>
    internal static string? FileVersionText(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return ReleaseVersion.Parse(fvi.ProductVersion) != null ? fvi.ProductVersion : fvi.FileVersion;
        }
        catch (Exception) { return null; }
    }

    /// <summary>This exe's version as <see cref="OlderRunningCopy"/> compares it (from the same version resource it reads for the others).</summary>
    internal static ReleaseVersion? OwnFileVersion() => ReleaseVersion.Parse(FileVersionText(Environment.ProcessPath));

    /// <summary>The processes named Clink (or like this exe) that can be looked at; one that cannot is left out. Never throws.</summary>
    internal static List<RunningClink> RunningClinks()
    {
        var found = new List<RunningClink>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Clink" };
        try { if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is { Length: > 0 } own) names.Add(own); } catch (Exception) { }
        foreach (string name in names)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); } catch (Exception) { continue; }
            foreach (var p in processes)
            {
                using (p)
                {
                    try
                    {
                        string? exe = p.MainModule?.FileName;
                        found.Add(new RunningClink(p.Id, p.SessionId, exe, FileVersionText(exe)));
                    }
                    catch (Exception) { }   // gone meanwhile, or not ours to look into (elevated)
                }
            }
        }
        return found;
    }

    /// <summary>The pipe command a command line asks for: "clip" (--clip), "trim" (--trim-last), "share" (--share-last), "record" (--record), "screenshot" (--screenshot), or null.</summary>
    public static string? CommandFromArgs(string[] args) =>
        args.Any(a => a.Equals("--clip", StringComparison.OrdinalIgnoreCase)) ? "clip"
        : args.Any(a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase)) ? "screenshot"
        : args.Any(a => a.Equals("--screenshot-edit-last", StringComparison.OrdinalIgnoreCase)) ? "screenshot-edit-last"
        : EditCommandFromArgs(args) is string edit ? edit
        : args.Any(a => a.Equals("--trim-last", StringComparison.OrdinalIgnoreCase)) ? "trim"
        : args.Any(a => a.Equals("--share-last", StringComparison.OrdinalIgnoreCase)) ? "share"
        : args.Any(a => a.Equals("--record", StringComparison.OrdinalIgnoreCase)) ? "record"   // toggles a manual recording (to a running Clink; a start finds the recorder not up yet and says so)
        : null;

    /// <summary>
    /// <c>--screenshot-edit &lt;path&gt;</c> as the pipe command <c>screenshot-edit &lt;full path&gt;</c>: the path is made absolute here, in the
    /// process that was given it (a running Clink has another current directory). Null without a path (the next argument is a
    /// switch or there is none) or when the path is not a valid one; the running Clink checks the rest (a readable .png).
    /// </summary>
    internal static string? EditCommandFromArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals("--screenshot-edit", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1])) return null;
            try { return "screenshot-edit " + Path.GetFullPath(args[i + 1].Trim()); }
            catch (Exception) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Gives up the single-instance mutex so a replacement process can take it (a restart, an update, or this process on its way
    /// out): released first, then closed. A mutex that is only closed stays owned by the thread that took it until that thread ends,
    /// and a process waiting for it would find it abandoned, or wait for nothing.
    /// </summary>
    public static void ReleaseSingleInstance()
    {
        var mutex = s_mutex;
        s_mutex = null;
        if (mutex == null) return;
        try { mutex.ReleaseMutex(); } catch (Exception) { }   // not ours to release (another thread owns it, an elevated Clink holds the real one)
        try { mutex.Dispose(); } catch (Exception) { }
    }

    // ------------------------------------------------------------------ a Clink that restarts itself

    private const string AfterPidSwitch = "--after-pid";

    /// <summary>The pid after the hidden <c>--after-pid</c> switch: the Clink that started this one by restarting itself. Null when there is none, or it is not a number.</summary>
    internal static int? AfterPid(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i + 1 < list.Count; i++)
            if (list[i].Equals(AfterPidSwitch, StringComparison.OrdinalIgnoreCase))
                return int.TryParse(list[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : null;
        return null;
    }

    /// <summary>
    /// Waits up to <paramref name="limit"/> for process <paramref name="pid"/> to be gone. True when it is (or never was there, or is
    /// this process, or cannot be waited for: do not hold the start up for that), false when it was still running at the end.
    /// A restarted Clink does this before it takes the mutex and opens the log, so it never runs next to the one it replaces,
    /// which may still be writing the files the restart changed. Never throws.
    /// </summary>
    internal static bool WaitForExit(int pid, TimeSpan limit)
    {
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(limit);
        }
        catch (Exception) { return true; }   // no such process any more, or no right to wait for it
    }

    /// <summary>The hidden switch a restart gives the new Clink: wait for this process to be gone first (see <see cref="WaitForExit"/>).</summary>
    internal static string[] AfterPidArguments() => new[] { AfterPidSwitch, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };

    /// <summary>The command line without any <c>--after-pid &lt;pid&gt;</c>: a restart of a restarted Clink waits for itself, not for the one before.</summary>
    private static IEnumerable<string> WithoutAfterPid(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(AfterPidSwitch, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < list.Count && int.TryParse(list[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _)) i++;
                continue;
            }
            yield return list[i];
        }
    }

    public static void SendCommand(string cmd)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", SessionPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            w.WriteLine(cmd);
            w.Flush();
        }
        catch (Exception ex) { Log.Warn("pipe send: " + ex.Message); }
    }

    private async Task PipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(SessionPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var r = new StreamReader(server);
                string? line = await r.ReadLineAsync(ct);
                if (line != null) _ = _ui.BeginInvoke(() => RunCommand(line));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("pipe server: " + ex.Message); await Task.Delay(500, ct).ContinueWith(_ => { }); }
        }
    }

    /// <summary>A control command from the pipe ("clip", "show", "trim", "share", "record", "screenshot", "pause", "resume"). UI thread.</summary>
    internal void RunCommand(string cmd)
    {
        if (EditPathOf(cmd) is string editPath)   // "screenshot-edit <path>": the path keeps its case
        {
            Screenshots.EditFile(editPath, explicitPath: true);
            return;
        }
        // Clips switched off (Settings → App → Features): the clip commands say so instead of failing quietly.
        if (!Settings.ClipsEnabled && IsClipsCommand(cmd))
        {
            ClipsOffNotice();
            return;
        }
        switch (cmd.Trim().ToLowerInvariant())
        {
            case "clip": ClipNow(); break;
            case "screenshot": Screenshots.CaptureRegion(Clink.Screenshots.ScreenshotTrigger.Cli); break;
            case "screenshot-edit-last": Screenshots.EditLast(); break;
            case "show": ShowRequested?.Invoke(); break;
            case "trim": TrimLastRequested?.Invoke(); break;
            case "share": ShareLast(); break;
            case "record": ToggleRecording(); break;
            case "pause": if (!IsPaused) TogglePause(); break;
            case "resume": if (IsPaused) TogglePause(); break;
            default: Log.Warn("pipe: unknown command " + (cmd.Length > 40 ? cmd[..40] + "…" : cmd)); break;
        }
    }

    /// <summary>
    /// "--clip" / "--share-last" / "--trim-last" on a start that found no Clink running: runs it once the app is up (at idle,
    /// after startup). A clip has nothing to save yet (nothing was recording), so that one says so instead of failing silently.
    /// Called during startup, before the pipe has delivered anything.
    /// </summary>
    internal void RunStartupCommand(string cmd)
    {
        if (!Settings.ClipsEnabled && IsClipsCommand(cmd)) { _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunCommand(cmd)); return; }   // the notice
        if (cmd == "record") { QueueRecordingAtLaunch(); return; }
        if (cmd != "clip") { _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunCommand(cmd)); return; }
        // Marked now, not at idle: a --share-last sent right behind this --clip can reach the pipe while Clink is still starting.
        _clipRefusedAt = DateTime.UtcNow;
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowBalloon(L.T("Clink started"), GameOnly
            ? L.T("It was not running, so there was nothing to save yet. It records while a game runs.")
            : L.T("It was not running, so there was nothing to save yet. It is recording now.")));
    }

    // ------------------------------------------------------------------ features (Settings → App → Features)

    /// <summary>What the clip commands say while the Clips feature is off.</summary>
    internal static string ClipsOffText => L.T("Clips are turned off — Settings → App → Features");

    /// <summary>A pipe / command-line command that needs the Clips feature (the replay buffer, recording, the clip library).</summary>
    internal static bool IsClipsCommand(string cmd) => cmd.Trim().ToLowerInvariant() is "clip" or "trim" or "share" or "record" or "pause" or "resume";

    /// <summary>The hotkey / tray / command line asked for a clip action while Clips is off: footer line, and a toast or balloon where the user is looking.</summary>
    private void ClipsOffNotice()
    {
        SayT("Clips are turned off — Settings → App → Features");
        NotifyProblem(L.T("Clips are off"), ClipsOffText);
    }

    /// <summary>How many engine starts / stops were asked for (test aid: with Clips off none is).</summary>
    internal int EngineStartGeneration => Volatile.Read(ref _startGen);

    /// <summary>The features changed (a toggle in Settings → App → Features). Raised on the UI thread.</summary>
    public event Action? FeaturesChanged;

    /// <summary>
    /// Turns the Clips feature (replay buffer, recording, clip library) on or off, live: off stops the engine (and any recording)
    /// and unbinds its hotkeys, hides its tray items and status dot; on starts the engine again. The last feature that is on cannot
    /// be turned off (false, nothing changed). A recording that was abandoned by a crash is still recovered at startup whatever
    /// this says (harmless: it only adds a finished file to the library).
    /// </summary>
    public bool SetClipsEnabled(bool on)
    {
        if (Settings.ClipsEnabled == on) return true;
        if (!on && !Settings.ScreenshotsEnabled) { SayT("At least one feature has to stay on"); return false; }
        Settings.ClipsEnabled = on;
        Settings.Save();
        Log.Info("features: Clips turned " + (on ? "on" : "off"));
        if (on) StartEngine();
        else
        {
            // A recording in progress is stopped properly first (the file is finished as a user stop, not cut by the engine going
            // down), and the user is told: the recording ends with the feature.
            bool recording = Engine.IsRecording || _wantRecording;
            if (recording) StopRecording(userRequested: false);
            EndRecordingWish(EngineStopCause.Pause);
            _recordWhenReadyUntil = DateTime.MinValue;
            if (Engine.IsRunning) StopEngine();
            else { Interlocked.Increment(ref _startGen); _starting = false; }   // a start that has not run yet is cancelled
            if (recording)
            {
                SayT("Recording stopped");
                Toasts.ShowNotice(L.T("Recording stopped"), L.T("Clips are turned off"));
            }
        }
        ApplyFeatures();
        return true;
    }

    /// <summary>Test aid: the "a manual recording is wanted" flag.</summary>
    internal bool WantRecordingForTests { get => _wantRecording; set => _wantRecording = value; }

    /// <summary>Turns the screenshot tools on or off, live (see <see cref="SetClipsEnabled"/>). The last feature that is on cannot be turned off.</summary>
    public bool SetScreenshotsEnabled(bool on)
    {
        if (Settings.ScreenshotsEnabled == on) return true;
        if (!on && !Settings.ClipsEnabled) { SayT("At least one feature has to stay on"); return false; }
        Settings.ScreenshotsEnabled = on;
        Settings.Save();
        Log.Info("features: Screenshots turned " + (on ? "on" : "off"));
        ApplyFeatures();
        return true;
    }

    /// <summary>Hotkeys, tray, status dot and the window follow the features.</summary>
    private void ApplyFeatures()
    {
        ReloadHotkeys();      // binds / unbinds the rows and updates the tray items
        ApplyStatusDot();
        RaiseStateChanged();
        FeaturesChanged?.Invoke();
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>
    /// A --dev instance does not record unless asked to (--dev-record): it would capture, encode and put toasts over the game the
    /// person running the test is playing. Paused is a state the tray, the window and the hotkeys already know how to leave. The
    /// first thing <see cref="Start"/> does, before anything is hooked, drawn or started.
    /// </summary>
    internal void ApplyStartPause()
    {
        if (!StartsPaused) return;
        IsPaused = true;
        Log.Info("dev: paused, nothing is recorded (--dev-record records)");
    }

    public void Start()
    {
        ApplyStartPause();
        Monitors = Native.Monitors.Enumerate();
        Library.Load(Settings.ClipsFolder);
        MigrateHotkeys();
        Hotkeys.Install(ActiveHotkeys, a => _ui.BeginInvoke(() => OnHotkey(a)));
        // Hardware encoder enumeration loads vendor DLLs and can take a while on some machines: warm it on a worker so the
        // window's codec list and the first engine start find the answer ready instead of probing on the UI thread.
        Task.Run(() => { try { _ = Capabilities.Codecs; } catch (Exception ex) { Log.Warn("codec probe: " + ex.Message); } });
        Engine.StateChanged += () => _ui.BeginInvoke(RaiseStateChanged);
        Engine.CaptureBorderDetermined += (state, detail) => { try { _ui.BeginInvoke(() => OnCaptureBorder(state, detail)); } catch (Exception ex) { Log.Warn("capture border: " + ex.Message); } };
        // Once, for the one engine this controller owns. Raised on the recorder's thread: only ever BeginInvoke here (an Invoke
        // would deadlock against a UI thread that waits for the recording to finish: StopRecordingForExit, Engine.Dispose).
        Engine.RecordingStopped += result => { try { _ui.BeginInvoke(() => OnRecordingEnded(result)); } catch (Exception ex) { Log.Warn("recording end: " + ex.Message); } };
        RecoverRecordingsAsync();
        _ = PipeServerAsync(_cts.Token);
        CreateTray();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), _ui);
        _timer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (Settings.RunAtStartup) SetRunAtStartup(true); // keep the Run entry pointing at wherever the exe lives now
        if (!Settings.ClipsEnabled) Log.Info("Clips are turned off (Settings → App → Features): the recorder is not started");
        else if (GameOnly) { Log.Info("Recording mode: only while a game or custom app runs"); PollGame(); }
        else StartEngine();
        Archive.Start();
        try { ShareCache.Current.StartSchedule(); }   // smaller share copies: cleaned up shortly after start and hourly, on a pool thread
        catch (Exception ex) { Log.Warn("share cache: cleanup not scheduled: " + ex.Message); }   // a bad root must not stop the start
        ScheduleAccountRefresh();   // Copium account name / Copium+ read once, a few seconds from now, in the background
        ApplyStatusDot();
        if (!IsDev) Updater.StartBackgroundChecks();   // a --dev instance never contacts the update feed (and a build without one never contacts anything)
        // A downloaded update was skipped at startup (install failed earlier, read-only folder, elevated): tell the user, once per start.
        if (Updater.InstallProblemNote is { } problem) { Say(problem); ShowBalloon(L.T("Clink update not installed"), problem.Shown); }
        // A hand edit of settings.json that does not parse, or has values that do not make sense (those keep their defaults).
        if (Settings.LoadProblem is string unread) { Say(Note.Verbatim(unread)); ShowBalloon(Settings.FileWasSkipped ? L.T("Clink settings not loaded") : L.T("Clink settings partly read"), unread); }   // (the text is English in every language: it names files and settings)
        // The footer line of MigrateHotkeys is easy to miss when Clink starts in the tray; this is a hotkey it just claimed or left unset.
        if (_hotkeyNotice is string hotkeyNotice) ShowBalloon(L.T("Clink hotkey"), hotkeyNotice);
        // Where the settings live. A Clink started from inside a packaged app (an MSIX/Store desktop app, say) sees that app's private copy of
        // AppData, so says so; a real one took another copy's settings over on a first start (App.RunNormal), or looks for a copy
        // worth offering (the tray item, below).
        if (AppDataRedirect.NoticeNote is { } redirected) Say(redirected);
        if (Settings.ImportedFrom is { } taken) SayT("Imported the settings Clink saved while started from inside {app} ({saved})", ("app", Note.Isolate(taken.FriendlyName)), ("saved", taken.SavedLocalText));
        // Settings were imported (now, or by the Clink that restarted into this one) but the clip lists could not be merged: said once,
        // and every later start tries the merge again.
        if (SettingsImport.TakeClipsNoticeNote() is { } clipsNotice) { Say(clipsNotice); ShowBalloon("Clink", clipsNotice.Shown); }
        // An import (now, or by the Clink that restarted into this one) did not take over a network folder: said once, naming which.
        if (SettingsImport.TakeNetworkPathsNoticeNote() is { } pathsNotice) { Say(pathsNotice); ShowBalloon("Clink", pathsNotice.Shown); }
        SettingsImport.PendingChanged += OnImportPending;
        if (CanOfferImport) _ = SettingsImport.Scan(Settings);
        RaiseStateChanged();
    }

    /// <summary>
    /// Another copy's settings are looked for by a real Clink with settings of its own (a fresh one has already taken the newest copy
    /// over) that is not itself inside a packaged app; a <c>--dev</c> instance looks only where <c>--packages-root</c> says, so a
    /// test never offers (or accepts) the settings of the person whose machine it runs on (<see cref="SettingsImport.CanOffer"/>).
    /// </summary>
    internal bool CanOfferImport => SettingsImport.CanOffer(Settings, IsDev);

    /// <summary>Once a second on the UI thread: game detection, the game-only start/stop rules, and a StateChanged for the stats line.</summary>
    private void Tick()
    {
        PollGame();
        var now = DateTime.UtcNow;
        if (GameOnly)
        {
            // The mode may have been switched while the engine was running with no game: give it the same grace as a game exit.
            if (_gamePid == 0 && Engine.IsRunning && !_stopping && _gameStopAt == null && !IsPaused) _gameStopAt = now + GameExitGrace;
            if (_gameStopAt is DateTime t && now >= t)
            {
                _gameStopAt = null;
                if (_gamePid == 0 && Engine.IsRunning) { Log.Info("No game running, stopping the recorder"); EndRecordingWish(EngineStopCause.GameEnded); StopEngine(); }
            }
        }
        // A recorder that should be running but is not: the mode was just switched, a start failed (encoder sessions taken by
        // another recorder, GPU or monitor still settling after resume / a display change) or it failed mid-run. Retry: 10 s
        // after a start that stopped without an error, 30 s / 30 s / 1 min / 2 min while starts keep failing; after a GPU reset
        // (driver crash / TDR, Engine.DeviceLost) a third of that, so recording resumes on a fresh device as soon as the driver
        // is back. StartEngine raises one notice per failure streak (NotifyProblem).
        if (_recordWhenReadyUntil != DateTime.MinValue && now >= _recordWhenReadyUntil)
        {
            // A start asked for while the recorder was not ready expired unfired (every other path resets it to MinValue).
            _recordWhenReadyUntil = DateTime.MinValue;
            SayT("Recording not started: the recorder did not get ready");
            NotifyProblem(L.T("Recording not started"), L.T("The recorder did not get ready"));
        }
        bool wanted = Settings.ClipsEnabled && !IsPaused && !_restarting && (!GameOnly || _gamePid != 0);
        int failedWait = Engine.DeviceLost ? 10 : 30;
        var backoff = TimeSpan.FromSeconds(Engine.Error == null ? 10 : failedWait << Math.Clamp(_startFailures - 1, 0, 2));
        if (wanted && !Engine.IsRunning && !_starting && now - _lastStartAt > backoff)
        {
            if (Engine.DeviceLost) SayT("The GPU was reset, restarting capture");
            if (Engine.Error != null) Log.Info("Retrying the recorder after: " + Engine.Error);
            StartEngine();
        }
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------ game detection (docs/SPEC-v2.md §5)

    /// <summary>Kicks off one detection pass on a worker thread (skipped while the previous one is still running).</summary>
    private void PollGame()
    {
        var mon = SelectedMonitor;
        if (mon == null) return;
        if (Interlocked.Exchange(ref _detecting, 1) == 1) return;
        uint pid = _gamePid;
        var custom = Settings.CustomApps?.ToArray() ?? Array.Empty<string>();
        Task.Run(() =>
        {
            bool alive = false;
            GameProcessTracker.GameResult? found = null;
            try
            {
                alive = pid != 0 && GameProcessTracker.IsAlive(pid);
                if (!alive) found = GameProcessTracker.DetectGame(mon, custom);
            }
            catch (Exception ex) { Log.Warn("game poll: " + ex.Message); }
            _ui.BeginInvoke(() => { _detecting = 0; OnGamePoll(pid, alive, found); });
        });
    }

    private void OnGamePoll(uint polledPid, bool alive, GameProcessTracker.GameResult? found)
    {
        if (polledPid != _gamePid) return;   // state moved on while we were looking
        if (alive) { _gameStopAt = null; return; }   // a game is "gone" when its process exits, not when it loses focus
        if (_gamePid != 0)
        {
            Log.Info($"Game exited: {CurrentGame}");
            GameProcessTracker.Forget(_gamePid);   // its pid may be handed to an unrelated process within seconds
            _gamePid = 0; CurrentGame = null; CurrentGameSource = null; _announceGame = null;
            if (GameOnly) _gameStopAt = DateTime.UtcNow + GameExitGrace;
            RaiseStateChanged();
        }
        if (found is { } g)
        {
            _gamePid = g.Pid; CurrentGame = g.Name; CurrentGameSource = g.Source; _gameStopAt = null;
            Log.Info($"Game detected: {g.Name} pid={g.Pid} ({g.Source})");
            if (!IsPaused && Settings.ClipsEnabled)
            {
                if (Engine.IsRunning && !_stopping) Toasts.ShowRecordingStarted(g.Name);     // always mode, or still in the grace period
                else
                {
                    // The toast follows once the engine is up: started here (game-only; this also overrides a grace-period stop
                    // that is still tearing down), or by Start()/Tick in always mode.
                    _announceGame = g.Name;
                    if (GameOnly) StartEngine();
                }
            }
            RaiseStateChanged();
        }
        // One game identity, published wherever it is used: the engine's Game audio track follows it, not only the focused
        // window (SPEC-v2 §5), and names the clip with it when it has no game of its own; a hotkey fires with extra modifiers
        // held (crouch + ScrollLock) only while it is in front.
        Engine.SetDetectedGame(_gamePid, CurrentGame);
        Hotkeys.GamePid = _gamePid;
    }

    // ------------------------------------------------------------------ updates

    /// <summary>Test seam: receives (title, text) of every <see cref="NotifyProblem"/> instead of a toast or balloon.</summary>
    internal Action<string, string>? ProblemSink { get; set; }

    private static bool MainWindowVisible => System.Windows.Application.Current?.MainWindow?.IsVisible == true;

    /// <summary>
    /// The user has the window in front of them: shown, not minimized, focused. Not <see cref="MainWindowVisible"/>, which a
    /// window minimized to the taskbar, or open behind a borderless game after an alt-tab, still is.
    /// </summary>
    private static bool MainWindowInFront =>
        System.Windows.Application.Current?.MainWindow is { IsVisible: true, IsActive: true, WindowState: not System.Windows.WindowState.Minimized };

    /// <summary>
    /// Why something asked for from the hotkey, the tray or the command line (no UI of their own) did not happen, shown once
    /// where the user is looking; the caller has already put it in the footer. Nothing more while the window is in front; a
    /// Clink toast (wrapped, at least 8 s) while the detected game is in front, since Windows holds tray balloons back over a
    /// game; a tray balloon otherwise. <paramref name="overGame"/>: the caller found a full-screen application in front that Clink
    /// did not detect as a game (<see cref="UserNotificationState"/>), which holds balloons back just the same.
    /// </summary>
    private void NotifyProblem(string title, string text, bool overGame = false)
    {
        if (ProblemSink is { } sink) { sink(title, text); return; }
        if (MainWindowInFront) return;
        if (overGame || Hotkeys.GameInFront()) Toasts.ShowProblem(title, text);
        else ShowBalloon(title, text);
    }

    private void OnUpdateAvailable(UpdateInfo info)
    {
        // With auto-update off this only happens from a manual check, and the version click opens the release page itself.
        Log.Info($"update: v{info.Version} ({info.Tag}) available{(Settings.AutoUpdate ? "" : " (auto-update is off)")}");
    }

    private void OnUpdateReady(ReleaseVersion version)
    {
        if (Updater.PendingVersion != version) return;   // cleared (or replaced) before this ran on the UI thread
        if (_updateItem != null) { _updateItem.Text = L.T("Update to v{version}", ("version", L.Ltr(version.ToString()))); _updateItem.Visible = true; }
        var problem = Updater.InstallProblemReason(Environment.ProcessPath ?? "");
        var msg = problem == null
            ? Note.Of("v{version} downloaded - installs on next start, or click 'Update to v{version}'", ("version", Note.Ltr(version.ToString())))
            : Note.Of("v{version} downloaded, but {problem}. Install it by hand from {folder}", ("version", Note.Ltr(version.ToString())), ("problem", problem), ("folder", Note.Ltr(Updater.UpdatesDir)));
        Say(msg);
        if (!MainWindowVisible) ShowBalloon(L.T("Clink update ready"), msg.Shown);
        UpdateStateChanged?.Invoke();
    }

    /// <summary>Update problems already announced in this session (one notification per version or unconfirmed streak, not one every 6 h).</summary>
    private readonly UpdateNoticeGate _updateNotices = new();

    /// <summary>
    /// A check pass ended (UI thread): its problem, when it is worth one (the download attempt cap of a version, background checks failing
    /// for days), is announced once per session (<see cref="NotifyProblem"/>), and the window shows the pass's result (UpdateStateChanged).
    /// </summary>
    private void OnCheckFinished(UpdateNotice? notice)
    {
        if (_disposed) return;
        if (_updateNotices.ShouldShow(notice))
        {
            Log.Info($"update: notifying '{notice!.TitleNote.English}': {notice.TextNote.English}");
            // The footer first: NotifyProblem stays silent while the window is in front, and the key is spent for the session either way.
            Say(notice.TextNote);
            NotifyProblem(notice.Title, notice.Text);
        }
        UpdateStateChanged?.Invoke();
    }

    /// <summary>The downloaded update was dropped (Dev Build switched): no "Update to v…" in the tray or the footer any more.</summary>
    private void OnPendingCleared()
    {
        if (Updater.PendingVersion != null) return;   // a newer download became pending meanwhile; OnUpdateReady shows that one
        if (_updateItem != null) _updateItem.Visible = false;
        UpdateStateChanged?.Invoke();
    }

    /// <summary>Installs the downloaded update now: hands over to the new exe and quits.</summary>
    public void UpdateNow() => _ = UpdateNowAsync();

    private bool _applying;   // UI thread: a click on Update is being handled (the check of the download is running or the apply was started)

    /// <summary>The body is fire-and-forget (<see cref="UpdateNow"/>): whatever it does not handle itself is logged and said, and the next click is not blocked, unless Quit has started.</summary>
    private async Task UpdateNowAsync()
    {
        try { await UpdateNowCoreAsync(); }
        catch (Exception ex)
        {
            Log.Error("update: unexpected failure", ex);
            if (_exiting || _disposed) return;
            SayT("Update failed: {error}", ("error", ex.Message));
            _applying = false;
        }
    }

    private async Task UpdateNowCoreAsync()
    {
        if (IsDev) { Say(Updater.DevModeNoteText); return; }   // the updates folder is the real Clink's, and the new process would not be this dev instance
        if (_applying) { SayT("Still checking…"); return; }   // a second click while the first one is still checking the download
        // A download made for the other channel (Dev Build switched since) is never installed; dropping it hides the button (PendingCleared).
        if (Updater.PendingVersion is { } v && !Updater.ShouldInstall(v, Updater.Current, Settings.UseDevBuilds, allowLeaveDev: true))
        {
            Updater.DropStalePending();
            SayT("v{version} was downloaded for the other update channel and is not installed - click the version in the footer to check again", ("version", Note.Ltr(v.ToString())));
            return;
        }
        string? pending = Updater.PendingPath;
        if (pending == null || Updater.PendingVersion == null || !File.Exists(pending)) { SayT("No update downloaded yet"); return; }
        var problem = Updater.InstallProblemReason(Environment.ProcessPath ?? "");
        if (problem != null) { SayT("Cannot install v{version}: {problem}. Install it by hand from {folder}", ("version", Note.Ltr(Updater.PendingVersion.Value.ToString())), ("problem", problem), ("folder", Note.Ltr(Updater.UpdatesDir))); return; }
        var version = Updater.PendingVersion.Value;
        _applying = true;
        Updater.ApplyToken token;
        try
        {
            Log.Info($"update: installing v{version} from {pending}");
            SayT("Checking v{version} before installing…", ("version", Note.Ltr(version.ToString())));
            // The hash of the whole file and the start-up check (up to 15 s) must not hold the window: off the UI thread.
            token = await Task.Run(() => Updater.PrepareApply(pending));
        }
        catch (Updater.ApplyRefusedException refused)
        {
            _applying = false;
            Log.Warn("update: not installed: " + refused.Message);
            Say(Updater.RefusalNote(refused, version));
            return;
        }
        catch (Exception ex)
        {
            _applying = false;
            Log.Error("update: not installed", ex);
            SayT("Update failed to start: {error}", ("error", Updater.UserErrorOf(ex)));
            return;
        }
        // The window was open for the whole check: the app may be quitting, or the channel switched / a newer download replaced this one.
        if (!StillTheUpdateToInstall(_exiting || _disposed, Updater.PendingPath, pending, version, Updater.Current, Settings.UseDevBuilds))
        {
            _applying = false;
            Log.Warn($"update: v{version} is no longer the update to install; not starting it");
            SayT("v{version} is no longer the update to install", ("version", Note.Ltr(version.ToString())));
            return;
        }
        try
        {
            // Come back the way we are now: visible if the window is open, otherwise in the tray (and as the same --dev instance, if
            // it is one), and only once this process is gone. The check was done above: not run a second time.
            Updater.StartApply(token, Environment.ProcessPath!, UpdateRestartArguments(MainWindowVisible));
        }
        catch (Exception ex)
        {
            _applying = false;
            Log.Error("update: launch failed", ex);
            SayT("Update failed to start: {error}", ("error", Updater.UserErrorOf(ex)));
            return;
        }
        Quit(userInitiated: false);   // _applying stays set: this process is on its way out
    }

    /// <summary>The decision after the awaited check of the download: the app is not going away, the pending update is still the file that was checked, and the channel / running exe still want its version.</summary>
    internal static bool StillTheUpdateToInstall(bool quitting, string? pendingPath, string preparedPath, ReleaseVersion version, ReleaseVersion current, bool useDevBuilds) =>
        !quitting
        && string.Equals(pendingPath, preparedPath, StringComparison.OrdinalIgnoreCase)
        && Updater.ShouldInstall(version, current, useDevBuilds, allowLeaveDev: true);

    /// <summary>
    /// What the Clink an update installs is started with: the window shown or in the tray as it is now, the same --dev instance if
    /// this is one, and <c>--after-pid</c> with this process, so it takes the mutex and the log only once this one has ended.
    /// </summary>
    internal static List<string> UpdateRestartArguments(bool windowVisible)
    {
        var args = new List<string> { windowVisible ? "--show" : "--minimized" };
        args.AddRange(DevSwitches());
        args.AddRange(AfterPidArguments());
        return args;
    }

    // ------------------------------------------------------------------ settings saved by another copy of Clink

    /// <summary>
    /// <see cref="SettingsImport"/> found (or lost) a copy of the settings, saved inside a packaged app's private AppData, that differs
    /// from these: the tray menu gets (or loses) "Import settings saved … (<app> copy)…", and the first time per start one balloon
    /// says so. UI thread.
    /// </summary>
    private void OnImportPending()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(OnImportPending); return; }
        var copy = SettingsImport.Pending;
        if (_importItem != null)
        {
            _importItem.Visible = copy != null;
            if (copy != null) _importItem.Text = L.T("Import settings saved {saved} ({app} copy)…", ("saved", copy.SavedLocalText), ("app", L.Isolate(copy.FriendlyName)));
        }
        if (copy == null) return;
        if (!_importOffered)
        {
            _importOffered = true;
            ShowBalloon("Clink", L.T("Settings from another copy of Clink found: right-click the tray icon to import"));
        }
        if (IsDev && DevAcceptImport && !_importAccepted)
        {
            _importAccepted = true;
            Log.Info("import: --dev-accept-import, importing now");
            ImportForeignSettings(copy);
        }
    }

    /// <summary>The tray item: says what the other copy would change and asks. Yes imports and restarts, No turns that copy down for good, Cancel leaves it for later.</summary>
    private void AskToImport()
    {
        var copy = SettingsImport.Pending;
        if (copy == null || _importInProgress) return;
        string text = ImportQuestion(copy);
        var owner = System.Windows.Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        TestGuard.CheckLaunch("a message box (import settings)");
        var answer = Dialog.Show(owner, text, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) ImportForeignSettings(copy);
        else if (answer == MessageBoxResult.No) SettingsImport.Decline(Settings, copy);
    }

    /// <summary>
    /// What the import question says (a Yes / No / Cancel message box). The Win32 buttons follow the Windows language, not Clink's, so the
    /// question does not name them: Yes imports and restarts, No keeps ours and stops asking about that copy, closing the box (Cancel) decides later.
    /// </summary>
    internal static string ImportQuestion(ForeignSettings copy)
    {
        var args = new[] { ("saved", (object?)copy.SavedLocalText), ("app", L.Isolate(copy.FriendlyName)), ("changes", SettingsImport.LabelList(copy.Differences)) };
        return copy.Differences.Count > 0
            ? L.T("Clink also has settings saved {saved} by a copy of Clink that was started from inside {app}.\n\nThey differ from yours in: {changes}.\n\nImport them and restart Clink? Your current settings are kept as settings.pre-import.json (or the next free settings.pre-import.N.json).\n\nIf you say no, Clink keeps your settings and stops asking about this copy. To decide later, cancel or close this message.", args)
            : L.T("Clink also has settings saved {saved} by a copy of Clink that was started from inside {app}.\n\nThey differ from yours in: a few settings.\n\nImport them and restart Clink? Your current settings are kept as settings.pre-import.json (or the next free settings.pre-import.N.json).\n\nIf you say no, Clink keeps your settings and stops asking about this copy. To decide later, cancel or close this message.", args);
    }

    private static readonly TimeSpan RecorderStopLimit = TimeSpan.FromSeconds(10);   // how long a restart waits for a recorder that is in the middle of starting

    private bool _importInProgress;   // an import has begun and has not failed: a second one is refused, the tray item is off
    private bool _restarting;         // a restart is under way: no clip is saved and the recorder is not started again

    /// <summary>An import is under way (it ends with this process, or with a message saying why not): the tray item is off and a second import is refused.</summary>
    internal bool ImportInProgress => _importInProgress;

    /// <summary>Starts the process a restart hands over to. The unit tests replace it with one that starts nothing.</summary>
    internal Action<ProcessStartInfo> StartProcess { get; set; } = psi => TestGuard.Start(psi)?.Dispose();

    /// <summary>What ends this process once its replacement is running (null: <see cref="Quit"/>). The unit tests replace it.</summary>
    internal Action? ExitForRestart { get; set; }

    /// <summary>The command line this process was started with, as a restart starts the next one from it (null: the real one). The unit tests replace it.</summary>
    internal IReadOnlyList<string>? CommandLine { get; set; }

    /// <summary>
    /// Takes over the settings (and the clip links) another copy of Clink saved in a packaged app's private AppData and restarts
    /// Clink to use them. In this order: a second import is refused from now on; saving a clip and starting the recorder are off;
    /// settings and clip index saves are frozen, so nothing this process still has pending (the window's timer, the updater, a clip
    /// being saved or shared) can put the old files back; the recorder and the archive are stopped; the import is made
    /// (<see cref="SettingsImport.Import"/>; what it replaces is kept beside it); and only then the new Clink is started, which waits
    /// for this process to be gone (<see cref="RestartClink"/>). If the import fails nothing is changed and Clink carries on. If the
    /// new Clink cannot be started either, saves are thawed and the recorder and archive go on, and the settings this Clink is running
    /// with are written back over the imported ones (so what is saved is what runs, not a trap for the next save) and the import is
    /// offered again; the user is told. UI thread (any other is marshalled).
    /// </summary>
    public void ImportForeignSettings(ForeignSettings copy)
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(() => ImportForeignSettings(copy)); return; }
        if (_importInProgress)
        {
            Log.Info("import: one is under way already: this request is refused");
            SayT("An import is already under way");
            return;
        }
        _importInProgress = true;
        if (_importItem != null) _importItem.Enabled = false;
        bool wasRecording = Engine.IsRunning;
        ImportResult result;
        try
        {
            StopForRestart();
            result = SettingsImport.Import(copy);
        }
        catch (Exception ex)
        {
            Log.Error("import: failed", ex);
            ContinueAfterFailedRestart(wasRecording);
            var why = Note.Of("Could not import those settings: {error}", ("error", ex.Message));
            Say(why);
            ShowBalloon("Clink", why.Shown);
            return;
        }
        if (result.ClipsNoticeNote is { } clipsNotice) Say(clipsNotice);
        else SayT("Imported the settings saved {saved}: restarting Clink", ("saved", copy.SavedLocalText));
        if (RestartClink(out string? error)) return;

        ContinueAfterFailedRestart(wasRecording);
        SettingsImport.ForgetPendingClipMerge();
        Settings.Save();
        SettingsImport.Offer(copy);   // at once: whoever called (the App tab's banner) looks at Pending as soon as this returns
        var text = Note.Of("Clink could not restart itself ({error}), so the import was not applied: your settings are as they were", ("error", error));
        Say(text);
        ShowBalloon("Clink", text.Shown);
    }

    /// <summary>
    /// Everything a restart must not have going on behind it: no clip is saved and the recorder is not started again (until
    /// <see cref="ContinueAfterFailedRestart"/>), settings and clip index saves are frozen, the recorder is stopped, and so is the
    /// archive (it moves clips and updates the index). A clip that is being saved at this moment keeps its file, which the next start
    /// finds in the clips folder, but its index entry is not written. Nothing here throws.
    /// </summary>
    private void StopForRestart()
    {
        _restarting = true;
        EndRecordingWish(EngineStopCause.Shutdown);
        Settings.FreezeSaves();
        Library.FreezeSaves();
        Interlocked.Increment(ref _startGen);   // a recorder start that has not begun is cancelled
        _starting = false;
        // A sign-in stops too: its token could not be saved any more (saves are frozen), so a late approval is revoked, not stored.
        try { _signIn?.Shutdown(); } catch (Exception ex) { Log.Warn("restart: sign-in: " + ex.Message); }
        try
        {
            if (_engineGate.Wait(RecorderStopLimit)) { try { Engine.Stop(); } finally { _engineGate.Release(); } }
            else Log.Warn("restart: the recorder was still starting; it is left to the shutdown");
        }
        catch (Exception ex) { Log.Warn("restart: could not stop the recorder: " + ex.Message); }
        try { Archive.Stop(); } catch (Exception ex) { Log.Warn("restart: could not stop the archive: " + ex.Message); }
    }

    /// <summary>The restart did not happen: saves are thawed, the archive runs again, the recorder too if it was recording, the tray item is back.</summary>
    private void ContinueAfterFailedRestart(bool restartRecorder)
    {
        Settings.ThawSaves();
        Library.ThawSaves();
        _restarting = false;
        try { Archive.Start(); } catch (Exception ex) { Log.Warn("restart: could not start the archive again: " + ex.Message); }
        if (restartRecorder) StartEngine();
        _importInProgress = false;
        if (_importItem != null) _importItem.Enabled = true;
        RaiseStateChanged();
    }

    /// <summary>
    /// Starts Clink again (<c>--show</c> and the same options, so a <c>--dev</c> instance comes back as that instance, and
    /// <c>--after-pid</c> with this process) and ends this one. The new Clink waits for this process to be gone (at most 10 s) before it
    /// takes the single-instance mutex or opens the log, so the two never run side by side; this one gives the mutex up as soon as the
    /// new one is started, so that if this process hangs on the way out the new Clink still gets it when its wait is over. False, and
    /// <paramref name="error"/> says why, when the new process could not be started: nothing has been given up then.
    /// </summary>
    internal bool RestartClink(out string? error)
    {
        error = null;
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (string a in RestartArguments(CommandLine ?? Environment.GetCommandLineArgs().Skip(1))) psi.ArgumentList.Add(a);
        foreach (string a in AfterPidArguments()) psi.ArgumentList.Add(a);
        try { StartProcess(psi); }
        catch (Exception ex)
        {
            Log.Error("restart: could not start Clink again", ex);
            error = ex.Message;
            return false;
        }
        Log.Info("restart: the new Clink is started (" + string.Join(' ', psi.ArgumentList) + "); this one ends");
        ReleaseSingleInstance();
        (ExitForRestart ?? (() => Quit(userInitiated: false)))();   // the new Clink is running already: unsaved editor edits are saved as a copy
        return true;
    }

    /// <summary>
    /// The command line of the restarted Clink: this one's, without what only makes sense once (a command, --minimized, --no-update,
    /// the <c>--after-pid</c> this one was given), with <c>--show</c>; and, for a <c>--dev</c> instance, its dev switches written out
    /// the same way every time (<see cref="DevSwitches"/>, whichever form it was started with, without the one-start
    /// <c>--dev-accept-import</c>). A restart that lost <c>--dev</c> would be a normal start: it takes the real Clink's mutex, or
    /// forwards a command to it. <see cref="RestartClink"/> adds its own <c>--after-pid</c>.
    /// </summary>
    internal static List<string> RestartArguments(IEnumerable<string> args)
    {
        var once = new[] { "--clip", "--trim-last", "--share-last", "--record", "--screenshot", "--screenshot-edit-last", "--minimized", "--no-update" };
        var kept = new List<string>();
        var all = WithoutAfterPid(DevOptions.WithoutSwitches(args)).ToList();
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Equals("--screenshot-edit", StringComparison.OrdinalIgnoreCase))   // with its path: a command for one start only
            {
                if (i + 1 < all.Count && !all[i + 1].StartsWith("--", StringComparison.Ordinal)) i++;
                continue;
            }
            if (!once.Contains(all[i], StringComparer.OrdinalIgnoreCase)) kept.Add(all[i]);
        }
        if (!kept.Contains("--show", StringComparer.OrdinalIgnoreCase)) kept.Add("--show");
        kept.AddRange(DevSwitches());
        return kept;
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _ui.BeginInvoke(async () =>
        {
            BeginTechnicalRestart(TimeSpan.FromMilliseconds(1500));   // at once, not after the delay: a recording that ends in between must not start its next part on the old engine
            await Task.Delay(1500);
            RefreshTrayIcon();   // a DPI change moves the tray's and the taskbar buttons' icon sizes
            AppIcons.RefreshWindows();
            Monitors = Native.Monitors.Enumerate();
            SayT("Display changed, restarting capture");
            MarkTechnicalRestart();
            StartEngine();
            ApplyStatusDot();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Monitor handles can change across sleep (a monitor that did not wake up, a dock): restart on fresh ones.
        if (e.Mode == PowerModes.Suspend) _ui.BeginInvoke(MarkTechnicalRestart);
        if (e.Mode == PowerModes.Resume) _ui.BeginInvoke(async () => { BeginTechnicalRestart(TimeSpan.FromMilliseconds(3000)); await Task.Delay(3000); Monitors = Native.Monitors.Enumerate(); MarkTechnicalRestart(); StartEngine(); });
    }

    public MonitorInfo? SelectedMonitor =>
        Monitors.FirstOrDefault(m => string.Equals(m.DeviceName, Settings.MonitorDevice, StringComparison.OrdinalIgnoreCase))
        ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();

    /// <summary>Audio tracks in a clip: the mix plus each enabled part (the size arithmetic in <see cref="Capabilities"/> needs the count).</summary>
    public int AudioTrackCount => 1 + (Settings.TrackGame ? 1 : 0) + (Settings.TrackMic ? 1 : 0) + (Settings.TrackVoice ? 1 : 0) + (Settings.TrackDesktop ? 1 : 0);

    /// <summary>
    /// The engine configuration from the settings. The bitrate is what the Capture tab shows: the manual value, or the one a
    /// size target picks for the replay length. <see cref="EngineConfig.Codec"/> is the user's choice as written; it is
    /// resolved against the GPU's encoders (<see cref="Capabilities.EffectiveCodec"/>) by <see cref="StartEngine"/> off the UI thread.
    /// </summary>
    public EngineConfig BuildConfig()
    {
        var mon = SelectedMonitor ?? throw new InvalidOperationException("No monitor found");
        (uint w, uint h) = OutputSize(mon, Settings.Resolution);
        int secs = Math.Clamp(Settings.BufferSeconds, 5, 3600);
        var (mbps, _) = Capabilities.ResolveBitrate(Settings, AudioTrackCount, secs);
        return new EngineConfig
        {
            Monitor = mon,
            OutWidth = w,
            OutHeight = h,
            Fps = (uint)Math.Clamp(Settings.Fps, 15, 120),
            BitrateMbps = Math.Clamp(mbps, Capabilities.MinTargetMbps, Capabilities.MaxTargetMbps),
            // Any case or spacing ("HEVC", "h.265") is accepted, like the combos do; AV1 goes through as "av1".
            Codec = EngineConfig.NormalizeCodec(Settings.Codec),
            GopSeconds = Math.Clamp(Settings.KeyframeSeconds, 0.25f, 5f),
            BufferSeconds = secs,
            CaptureCursor = Settings.CaptureCursor,
            TrackDesktop = Settings.TrackDesktop, TrackGame = Settings.TrackGame, TrackMic = Settings.TrackMic, TrackVoice = Settings.TrackVoice,
            AllDeviceId = Settings.AllDeviceId,
            MicDeviceId = Settings.MicDeviceId,
            GameProcess = GameProcessTracker.NormalizeName(Settings.GameProcess),
            VoiceApp = GameProcessTracker.NormalizeName(Settings.VoiceApp),
            HdrKnee = Settings.HdrKnee, HdrPeak = Settings.HdrPeak,
            ClipsFolder = Settings.ClipsFolder,
            EncoderTuning = Clink.Encode.EncoderExperiments.Resolve(Settings.EncoderExperiment, Capabilities.SizeTargetBytes(Settings.SizeTarget) > 0),
        };
    }

    public static (uint w, uint h) OutputSize(MonitorInfo mon, string resolution)
    {
        int targetH = resolution.Trim().ToLowerInvariant() switch { "1440p" => 1440, "1080p" => 1080, "720p" => 720, _ => 0 };
        double w = mon.Width, h = mon.Height;
        // The preset names the short side: a portrait 1080×1920 monitor is already 1080p, and its 720p is 720×1280.
        double shortSide = Math.Min(w, h);
        if (targetH > 0 && targetH < shortSide) { double k = targetH / shortSide; w *= k; h *= k; }
        return ((uint)Math.Round(w / 2) * 2, (uint)Math.Round(h / 2) * 2);
    }

    // ------------------------------------------------------------------ engine

    /// <summary>
    /// (Re)starts the engine with the current settings. In game-only mode with no game running this stops it instead, so the
    /// UI can call it after any settings change (including the recording mode) without caring which mode is on.
    /// </summary>
    public void StartEngine()
    {
        _technicalRestartUntil = DateTime.MinValue;   // the restart that was waited for is here (or will not come: paused, restarting)
        if (!Settings.ClipsEnabled)
        {
            // Clips are off (Settings → App → Features): the replay buffer is never started; one that runs is stopped.
            if (Engine.IsRunning && !_stopping) { EndRecordingWish(EngineStopCause.Pause); StopEngine(); }
            RaiseStateChanged();
            return;
        }
        if (IsPaused || _restarting) return;   // a restart is under way: the recorder was stopped for it and stays stopped
        if (GameOnly && _gamePid == 0)
        {
            if (Engine.IsRunning) { EndRecordingWish(EngineStopCause.GameEnded); StopEngine(); }
            RaiseStateChanged();
            return;
        }
        int gen = Interlocked.Increment(ref _startGen);
        _settingsRestartPending = false;   // this start reads the settings as they are now
        _lastStartAt = DateTime.UtcNow;   // also when the config fails: Tick retries on its backoff, not every second
        EngineConfig cfg;
        try { cfg = BuildConfig(); }
        catch (Exception ex) { SayT("Cannot start: {error}", ("error", ex.Message)); if (_stopping) StopEngine(); return; }   // the new generation cancelled that stop: re-issue it
        _stopping = false;
        _starting = true;
        Task.Run(() =>
        {
            _engineGate.Wait();
            try
            {
                if (gen != _startGen) return;
                // A codec this GPU cannot encode (or H.264 above 4096 px) falls back to one it can (the Capture tab says so); the
                // probe runs here, not on the UI thread.
                try
                {
                    string effective = Capabilities.EffectiveCodec(cfg.Codec, cfg.OutWidth, cfg.OutHeight);
                    if (effective != cfg.Codec)
                    {
                        Log.Info(Capabilities.IsSupported(cfg.Codec)
                            ? $"H.264 cannot encode {cfg.OutWidth}x{cfg.OutHeight}, recording {effective}"
                            : $"Codec {cfg.Codec} has no hardware encoder here, recording {effective}");
                        cfg.Codec = effective;
                    }
                }
                catch (Exception ex) { Log.Warn("codec check: " + ex.Message); }
                try { Engine.Start(cfg); }
                catch (Exception ex) { Log.Error("Engine start", ex); }
            }
            finally { _engineGate.Release(); }
            _ui.BeginInvoke(() =>
            {
                if (gen == _startGen)
                {
                    // Only the newest start reports: a superseded one would show an older start's error.
                    _starting = false;
                    if (Engine.IsRunning)
                    {
                        _startFailures = 0;
                        _engineRunGen++;
                        StartQueuedRecording();
                    }
                    else if (Engine.ErrorInfo is { } failed)
                    {
                        SayT("Recording failed: {error}", ("error", EngineStatusText.ErrorNote(failed)));
                        // The window is usually in the tray while a game runs, so the footer alone is invisible. Once per
                        // streak: Tick keeps retrying.
                        if (_startFailures++ == 0) NotifyProblem(L.T("Clink is not recording"), EngineStatusText.Error(failed));
                    }
                }
                if (Engine.IsRunning && _announceGame is string game) { _announceGame = null; Toasts.ShowRecordingStarted(game); }
                RaiseStateChanged();
            });
        });
    }

    private void StopEngine()
    {
        int gen = Interlocked.Increment(ref _startGen);   // a start that has not run yet is cancelled; a later start cancels this stop
        _starting = false;
        _startFailures = 0;
        _stopping = true;
        Task.Run(() =>
        {
            bool stopped = false;
            _engineGate.Wait();
            try { if (gen == _startGen) { Engine.Stop(); stopped = true; } }
            catch (Exception ex) { Log.Warn("Engine stop: " + ex.Message); }
            finally { _engineGate.Release(); }
            // The recorder is idle now: give the last clips' buffers back to the OS. They are gen2 / large-object garbage that
            // an idle tray app may not collect for hours (a ~20 ms pause here costs nothing).
            if (stopped) { try { GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true); } catch { } }
            _ui.BeginInvoke(() =>
            {
                if (gen == _startGen) _stopping = false;
                RaiseStateChanged();
            });
        });
    }

    public void TogglePause()
    {
        if (!Settings.ClipsEnabled) { ClipsOffNotice(); return; }
        IsPaused = !IsPaused;
        if (IsPaused) { EndRecordingWish(EngineStopCause.Pause); StopEngine(); SayT("Recording paused"); }
        else
        {
            if (GameOnly && _gamePid == 0) SayT("Recording resumed: waiting for a game"); else SayT("Recording resumed");
            _announceGame = CurrentGame;   // "Recording started · Game" once the engine is back up, when a game is running
            StartEngine();
        }
        if (_pauseItem != null) _pauseItem.Text = PauseItemText;
        RaiseStateChanged();
    }

    /// <summary>The tray's pause item: "Resume recording" while paused, else "Pause recording".</summary>
    private string PauseItemText => IsPaused ? L.T("Resume recording") : L.T("Pause recording");

    /// <summary>Shows <paramref name="note"/> in the language of the UI and logs it in English.</summary>
    public void Say(Note note)
    {
        string shown = note.Shown;
        LastNotice = shown;
        Log.Info("notice: " + note.English);
        Notice?.Invoke(shown);
    }

    /// <summary>Catalog key (its English text) with <c>{name}</c> arguments: shows the translation, logs the English.</summary>
    public void SayT(string key, params (string Name, object? Value)[] args) => Say(Note.Of(key, args));

    /// <summary>Whether Windows' yellow capture border is hidden, as of the last capture start (Hidden until one reports).</summary>
    public CaptureBorderState CaptureBorder { get; private set; }
    public string CaptureBorderDetail { get; private set; } = "";
    public bool CaptureBorderKnown { get; private set; }
    public event Action? CaptureBorderChanged;

    /// <summary>
    /// The engine reported the border outcome (UI thread). When it stays, a toast says so once per app launch until the user
    /// presses "Don't show again" (<see cref="Settings.CaptureBorderNoticeDismissed"/>); the Capture tab's status line always shows
    /// it. The toast is short and the detail lives on that line and in the log.
    /// </summary>
    internal static bool ShouldNotify(CaptureBorderState state, bool dismissed, bool shownThisRun) =>
        state is not (CaptureBorderState.Hidden or CaptureBorderState.Unconfirmed) && !dismissed && !shownThisRun;

    private bool _borderNoticeShownThisRun;

    private void OnCaptureBorder(CaptureBorderState state, string detail)
    {
        CaptureBorder = state;
        CaptureBorderDetail = detail;
        CaptureBorderKnown = true;
        CaptureBorderChanged?.Invoke();
        if (!ShouldNotify(state, Settings.CaptureBorderNoticeDismissed, _borderNoticeShownThisRun)) return;
        ShowBorderToast(state);
    }

    private void ShowBorderToast(CaptureBorderState state)
    {
        _borderNoticeShownThisRun = true;
        Action dontShowAgain = () => { Settings.CaptureBorderNoticeDismissed = true; Settings.Save(); };
        var (title, text) = BorderNoticeTexts(state);
        Toasts.ShowBorderNotice(title, text, dontShowAgain, state == CaptureBorderState.Denied ? OpenCaptureBorderSetting : null);
    }

    /// <summary>The title and line of the yellow-border toast for a border state that stays (<see cref="ShouldNotify"/>).</summary>
    internal static (string Title, string Text) BorderNoticeTexts(CaptureBorderState state) => state switch
    {
        CaptureBorderState.UnsupportedOs => (L.T("Yellow border around the screen?"), L.T("Windows 11 is needed to hide it. Not in your clips.")),
        CaptureBorderState.Denied => (L.T("Yellow border around the screen?"), L.T("Allow it in Windows settings, then pause/resume.")),
        _ => (L.T("Couldn't hide the capture border"), L.T("Clips are unaffected. Details: Capture tab, log.")),
    };

    /// <summary>Opens Windows' "Screenshot borders" settings page.</summary>
    internal static void OpenCaptureBorderSetting()
    {
        const string uri = "ms-settings:privacy-graphicscapturewithoutborder";
        try { TestGuard.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Could not open " + uri + ": " + ex.Message); }
    }

    /// <summary>The "Don't show again" flags that Reset clears: the border notice and the delete clip question.</summary>
    internal static void ClearDismissedFlags(Settings s)
    {
        s.CaptureBorderNoticeDismissed = false;
        s.ClipDeleteConfirmDismissed = false;
    }

    /// <summary>Brings back every notice the user dismissed with "Don't show again"; the border notice shows right away if it applies.</summary>
    public void ResetDismissedNotices()
    {
        ClearDismissedFlags(Settings);
        _borderNoticeShownThisRun = false;
        Settings.Save();
        Toasts.CloseKind("border");
        if (CaptureBorderKnown && ShouldNotify(CaptureBorder, false, false)) ShowBorderToast(CaptureBorder);
        else SayT("Dismissed notices will show again.");
    }

    /// <summary>Updates the tray tooltip / menu and the status dot, then raises <see cref="StateChanged"/>. UI thread.</summary>
    private void RaiseStateChanged()
    {
        UpdateTray();
        if (_recordItem != null) _recordItem.Text = RecordItemText;
        if (_dot != null && Settings.StatusDot && Settings.ClipsEnabled) { try { _dot.Apply(State, IsManualRecording); } catch { } }
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ hotkeys (docs/SPEC-v2.md §6)

    /// <summary>
    /// Settings.Load() has already put the hotkey list in shape (<c>Hotkeys[0]</c> is the main hotkey, see
    /// <see cref="Settings.EnsureHotkeyModel"/>) and noted what changed: a settings file of an older schema, a first start or a list
    /// that had to be fixed is saved once here, in the current layout. When no hotkey saved the full replay, the user is told once
    /// which key the main hotkey got, or that it still needs one.
    /// </summary>
    private void MigrateHotkeys()
    {
        if (Settings.Migrated)
        {
            Settings.Save();
            if (Settings.SchemaMigrated) Log.Info("settings: migrated to schema 3");
            if (Settings.OfferedHotkeyDefaults.Count > 0)
                Log.Info($"settings: hotkey defaults offered ({string.Join(", ", Settings.OfferedHotkeyDefaults)}){(Settings.AddedHotkeyDefaults.Count > 0 ? $", added: {string.Join(", ", Settings.AddedHotkeyDefaults)}" : ", none added (their keys or actions were taken)")}");
        }
        if (Settings.MainHotkeyAssigned) { var n = Note.Of("{keys} now saves the full replay. Change it under Capture → Hotkeys", ("keys", Note.Ltr("Alt+C"))); Say(n); _hotkeyNotice = n.Shown; }
        else if (Settings.MainHotkeyNeedsSetup) { var n = Note.Of("Set your main hotkey under Capture → Hotkeys: it saves the full replay"); Say(n); _hotkeyNotice = n.Shown; }
        // A key Clink just took over (bare PrintScreen used to be Windows' own screenshot): said once, like the main hotkey above.
        if (SeededHotkeyNote(Settings) is { } seededNote)
        {
            Say(seededNote);
            string seeded = seededNote.Shown;
            _hotkeyNotice = _hotkeyNotice == null ? seeded : _hotkeyNotice + "\n" + seeded;
        }
    }

    /// <summary>What to tell the user about default hotkeys this load added as rows (<see cref="Settings.AddedHotkeyDefaults"/>); null when none was.</summary>
    internal static string? SeededHotkeyNotice(Settings s) => SeededHotkeyNote(s)?.Shown;

    private static Note? SeededHotkeyNote(Settings s)
    {
        if (!s.ScreenshotsEnabled || (IsDev && !DevHotkeys)) return null;   // no tools, or a --dev instance whose hook binds nothing: the key is not taken over
        if (s.AddedHotkeyDefaults.Contains("screenshot", StringComparer.OrdinalIgnoreCase))
        {
            string keys = ScreenshotHotkeyLabelFor(s.Hotkeys) ?? "PrintScreen";
            return Note.Of("{keys} now takes a Clink screenshot — change it under Capture → Hotkeys", ("keys", Note.Ltr(keys)));
        }
        if (s.AddedHotkeyDefaults.Contains("screenshot-edit-last", StringComparer.OrdinalIgnoreCase))   // an install that already had the screenshot key
        {
            string keys = ScreenshotEditHotkeyLabelFor(s.Hotkeys) ?? "Shift+PrintScreen";
            return Note.Of("{keys} now edits your last Clink screenshot — change it under Capture → Hotkeys", ("keys", Note.Ltr(keys)));
        }
        return null;
    }

    /// <summary>
    /// The bindings the keyboard hook gets: <see cref="Settings.Hotkeys"/>, except in a <c>--dev</c> instance, which binds nothing
    /// unless started with <c>--dev-hotkeys</c>, so it never swallows a combo the real Clink (or a game) is waiting for. The hook is
    /// installed either way, which is all the hotkey editor's key capture needs.
    /// </summary>
    private IReadOnlyList<HotkeyAction> ActiveHotkeys => IsDev && !DevHotkeys ? Array.Empty<HotkeyAction>() : BindableHotkeys(Settings);

    /// <summary>
    /// What the hook may bind: all of <see cref="Settings.Hotkeys"/>, except that with the screenshot tools off the screenshot rows
    /// stay in the list (and in settings.json) but are not handed to the hook, so their keys (PrintScreen) are left to Windows. A
    /// "screenshot-edit-last" row follows the same rule (it opens the editor). A row with an unknown action stays in the list but is not bound either.
    /// </summary>
    internal static IReadOnlyList<HotkeyAction> BindableHotkeys(Settings s) =>
        s.Hotkeys.Where(h => CommandFor(h.Action) switch
        {
            HotkeyCommand.Unknown => false,   // an action this version does not know does nothing when pressed: it must not swallow the key either
            var c when IsScreenshotCommand(c) => s.ScreenshotsEnabled,
            // Clips off: the clip, pause, record and share-last rows stay in the list (and settings.json) but their keys go back to the system.
            // (Cloud sharing off does not unbind share-last: the press says why nothing was shared.)
            _ => s.ClipsEnabled,
        }).ToList();

    private static bool IsScreenshotCommand(HotkeyCommand c) => c is HotkeyCommand.Screenshot or HotkeyCommand.ScreenshotEditLast;

    /// <summary>Re-reads <see cref="Settings.Hotkeys"/> after the list changed in the UI (an unset main hotkey and no other row means no hotkeys).</summary>
    public void ReloadHotkeys()
    {
        Hotkeys.Rebind(ActiveHotkeys);
        UpdateTrayHotkey();
    }

    /// <summary>The keys that save a clip, for labels ("Press Alt+C to save a clip"): the main hotkey's, else the first additional "clip" hotkey that is set; null when neither is (the main may be unset by choice).</summary>
    public string? ClipHotkeyLabel => ClipHotkeyLabelFor(Settings.Hotkeys);

    /// <summary><see cref="ClipHotkeyLabel"/> for a hotkey list: <c>[0]</c> is the main hotkey; any "clip" row after it with keys is the fallback.</summary>
    internal static string? ClipHotkeyLabelFor(IReadOnlyList<HotkeyAction> hotkeys)
    {
        if (hotkeys.Count > 0 && hotkeys[0].Keys.Vk > 0) return hotkeys[0].Keys.ToString();
        return hotkeys.Skip(1).FirstOrDefault(h => Hotkeys.IsClip(h.Action) && h.Keys.Vk > 0)?.Keys.ToString();
    }

    /// <summary>"Clip now (F9)" for the tray menu, or just "Clip now" without a binding.</summary>
    private string ClipTrayText => ClipHotkeyLabel is string l ? L.T("Clip now ({keys})", ("keys", L.Ltr(l))) : L.T("Clip now");

    /// <summary>What a hotkey row does, by <see cref="HotkeyAction.Action"/> (see <see cref="CommandFor"/>).</summary>
    internal enum HotkeyCommand { Clip, Pause, ShareLast, Record, Screenshot, ScreenshotEditLast, Unknown }

    /// <summary>
    /// The command of a hotkey row's action text (case-insensitive, trimmed). Null, blank and "clip" are the clip; "share" is
    /// "share-last". Any other text is <see cref="HotkeyCommand.Unknown"/> and is ignored when pressed: before v0.4 it fell through to
    /// saving a clip, so a typo in a hand-edited row clipped. (<see cref="Hotkeys.IsClip"/> still decides which rows are clip rows.)
    /// </summary>
    internal static HotkeyCommand CommandFor(string? action) => (action ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "clip" => HotkeyCommand.Clip,
        "pause" => HotkeyCommand.Pause,
        "share-last" or "share" => HotkeyCommand.ShareLast,
        "record" => HotkeyCommand.Record,
        "screenshot" => HotkeyCommand.Screenshot,
        "screenshot-edit-last" => HotkeyCommand.ScreenshotEditLast,
        _ => HotkeyCommand.Unknown,
    };

    private void OnHotkey(HotkeyAction a)
    {
        switch (CommandFor(a.Action))
        {
            case HotkeyCommand.Pause:
                TogglePause();
                // In a game nothing else shows it: a press by mistake would stop recording without a sign. A resume with a game
                // running gets its "Recording started" toast once the engine is up.
                if (!MainWindowInFront && Settings.ToastRecording.Enabled && (IsPaused || CurrentGame == null))
                    Toasts.ShowNotice(IsPaused ? L.T("Recording paused") : L.T("Recording resumed"),
                        IsPaused ? L.T("Press {keys} to resume", ("keys", L.Ltr(a.Keys.ToString()))) : GameOnly ? L.T("Waiting for a game") : L.T("Replay buffer restarted"), paused: IsPaused);
                break;
            case HotkeyCommand.ShareLast: ShareLast(); break;
            case HotkeyCommand.Record: ToggleRecording(); break;
            case HotkeyCommand.Screenshot: Screenshots.CaptureRegion(Clink.Screenshots.ScreenshotTrigger.Hotkey); break;
            case HotkeyCommand.ScreenshotEditLast: Screenshots.EditLast(); break;
            case HotkeyCommand.Clip: ClipNow(a.Seconds > 0 ? a.Seconds : null); break;
            default: Log.Warn($"hotkey: unknown action '{a.Action}' on {a.Keys}: ignored"); break;
        }
    }

    // ------------------------------------------------------------------ clips

    /// <summary>Why a clip or a recording cannot start now (the recorder is not up): one sentence for the footer and the notification.</summary>
    private Note NotRecordingNote(bool waitingForGame) =>
        _restarting ? Note.Of("Clink is restarting")
        : IsPaused ? Note.Of("Recording is paused")
        : waitingForGame ? Note.Of("Not recording: waiting for a game")
        : Engine.ErrorInfo is { } err && !_starting ? Note.Of("Recording failed: {error}", ("error", EngineStatusText.ErrorNote(err)))
        : Note.Of("Not recording yet");

    /// <param name="seconds">Replay length to save; null = the replay-length slider (<see cref="Settings.BufferSeconds"/>).</param>
    public async void ClipNow(int? seconds = null)
    {
        if (!Settings.ClipsEnabled) { ClipsOffNotice(); return; }
        if (_restarting || !Engine.IsRunning)
        {
            bool waiting = !IsPaused && GameOnly && _gamePid == 0;
            // A recorder that failed to start says why: "yet" would promise a start that may never come.
            var why = NotRecordingNote(waiting);
            _clipRefusedAt = DateTime.UtcNow;   // a share-last right behind this press must not share the clip before it
            Say(why);
            // The hotkey, the tray item and --clip have no UI of their own and the window is usually in the tray, minimized or
            // behind the game while one runs: a footer line alone would make a missed clip look saved, so say it where the user
            // notices unless the window is in front of them (with an error sound when the click sound is on: no click played).
            if (!MainWindowInFront)
            {
                if (Settings.ClickSound) ClickSound.PlayError();
                NotifyProblem(L.T("Clip not saved"), waiting ? L.T("Not recording: waiting for a game. If your game is not detected, add it under Capture → Recording → Custom apps.") : why.Shown);
            }
            return;
        }
        if ((DateTime.UtcNow - _lastClipAt).TotalMilliseconds < 700) return;
        if (Interlocked.Exchange(ref _saving, 1) == 1) return;
        _lastClipAt = DateTime.UtcNow;
        _clipRefusedAt = DateTime.MinValue;
        var saved = new TaskCompletionSource<ClipEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _saveTask = saved.Task;
        if (Settings.ClickSound) ClickSound.Play(Settings.ClickVolume);
        try
        {
            int? length = seconds is int s && s > 0 ? Math.Clamp(s, 1, 3600) : null;
            // The engine names the clip after its own game; the app's detection fills in when it has none (device Game track, custom app).
            var result = await Engine.SaveClipAsync(length, CurrentGame);
            // The names of the tracks the file really has, in file order (the trim editor labels and keeps tracks by them).
            var entry = Library.Add(result.Path, result.DurationSeconds, result.SizeBytes, result.Game, result.AudioTracks);
            saved.TrySetResult(entry);
            SayT("Clip saved ({seconds}s)", ("seconds", result.DurationSeconds.ToString("0", L.FormatCulture)));
            Toasts.ShowClipSaved(entry);
            Archive.NotifyClipSaved();
            ClipSaved?.Invoke(entry);
            // The save copied the clip into managed buffers that are all garbage now: a background GC lets the next save reuse
            // that space instead of growing the process by another clip.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        }
        catch (Exception ex)
        {
            Log.Error("Save clip", ex);
            SayT("Clip failed: {error}", ("error", UserText.NoteOf(ex)));
            // The click has already played: without this a failed save looks like a saved clip.
            NotifyProblem(L.T("Clip not saved"), UserText.Of(ex));
        }
        finally { saved.TrySetResult(null); _saving = 0; }
    }

    /// <summary>
    /// Shares the newest clip (the "share last clip" hotkey / tray item): uploads it, or copies its link if it already has one. With
    /// no Copium token it opens the share window for the file (see <see cref="ShareAsync"/>), except over the detected game, where it
    /// only says to open Clink.
    /// </summary>
    public async void ShareLast()
    {
        // The hotkey, the tray item and --share-last: a notice instead of an upload while Clips is off or cloud sharing is off.
        if (!Settings.ClipsEnabled) { ClipsOffNotice(); return; }
        if (!Settings.CloudSharingEnabled)
        {
            // A clip that already has a link only has it copied (no network), so that stays possible; anything else is refused. A clip
            // being saved right now (or just turned down) has no link yet.
            var newest = Volatile.Read(ref _saving) == 1 || DateTime.UtcNow - _clipRefusedAt < TimeSpan.FromSeconds(3) ? null : NewestShareable();
            if (newest == null || string.IsNullOrEmpty(newest.CopiumLink))
            {
                Say(CloudOffNote);   // (worded by ConsentGate)
                Log.Info("share-last: cloud sharing is off");
                NotifyProblem(L.T("Cloud sharing is off"), CloudOffText, overGame: UserNotificationState.FullScreenInFront());
                return;
            }
        }
        // "Clip, then share" (a Stream Deck multi-action, --clip then --share-last, two quick presses) means the clip being saved
        // right now: the library only gets it once the save is done, so wait for that save, however long it takes (a long replay
        // on a slow disk), and share what it saved, never the clip before it. A clip turned down before its save started (not
        // recording, or --clip starting Clink) shares nothing either: the two presses of a multi-action arrive well inside 3 s,
        // and a share-last pressed on its own still shares the newest clip.
        ClipEntry? entry;
        if (Volatile.Read(ref _saving) == 1 && _saveTask is { } saving)
        {
            entry = await saving;
            // ClipNow has already said why the clip was not saved (footer, and a toast / balloon away from the window).
            if (entry == null) { Log.Info("share-last: the clip being saved failed; nothing shared"); return; }
        }
        else if (DateTime.UtcNow - _clipRefusedAt < TimeSpan.FromSeconds(3))
        {
            // Same: ClipNow / the startup balloon has said why; the footer keeps that reason.
            Log.Info("share-last: the clip just pressed was not saved; nothing shared");
            return;
        }
        else entry = NewestShareable();
        bool ok = false, joined = false, windowOpened = false;
        string? afterCopy = null;   // this call's own "declined after the smaller copy was made" reason (not the shared LastShareOutcome)
        if (entry == null) SayT("No clips to share yet");
        else
        {
            // A second press during the same upload gets the same result: the press that started it reports a failure, once.
            joined = _uploads.TryGetValue(entry, out var running) && running.Announced;
            ok = await ShareAsync(entry, announce: true, context: ConsentContext.ShareLast, declinedAfterCopy: reason => afterCopy = reason);
            // no Copium token: the share window is the answer, not a problem; the user's own "Not now" is not one either
            windowOpened = LastShareOutcome is ShareOutcome.FileShareOpened or ShareOutcome.Declined;
        }
        // The user answered no question here: the copy took minutes and the sign-in, plan or switches changed meanwhile (the startup
        // account check can do it unasked). Without this the hotkey shows "Making a smaller copy…" and then nothing.
        if (afterCopy != null) { NotifyProblem(L.T("Clip not uploaded"), afterCopy, overGame: UserNotificationState.FullScreenInFront()); return; }
        // The hotkey, the tray item and --share-last have no UI of their own, and the window is usually in the tray, minimized or
        // behind the game: a refusal (or a failure) that only reached the footer would be invisible (NotifyProblem: a toast over
        // the game, a tray balloon otherwise).
        if (!ok && !joined && !windowOpened && LastNotice.Length > 0) NotifyProblem(L.T("Clip not uploaded"), LastNotice, overGame: UserNotificationState.FullScreenInFront());
    }

    /// <summary>The newest clip "share last clip" means (a recording is hours long: not that), or null.</summary>
    private ClipEntry? NewestShareable() => Library.Entries.FirstOrDefault(e => !IsRecordingEntry(e) && (e.IsShared || e.IsArchived || e.Exists));

    /// <summary>
    /// Why a clip may not go to Copium with this account, or null when it fits: "Over Copium's free limit (200 MB / 10 min)"
    /// without Copium+, "Over Copium Plus's limit (2 GB / 60 min)" with it (<see cref="Capabilities.CopiumLimits"/>). Copium would
    /// refuse it after a long upload; Share says so up front. A clip exactly at a limit passes. Pure.
    /// </summary>
    internal static string? ShareRefusalFor(long size, double duration, bool plus)
        => ShareSizeOver(size, plus) ? OverLimitNote(plus).Shown : ShareLengthRefusalFor(duration, plus);

    /// <summary>"Over Copium's free limit (200 MB / 10 min)" / "Over Copium Plus's limit (2 GB / 60 min)" as a note (shown translated, logged in English).</summary>
    private static Note OverLimitNote(bool plus)
    {
        var (maxBytes, maxSeconds, _) = Capabilities.CopiumLimits(plus);
        var limits = new[] { ("size", (object?)Note.Size(maxBytes)), ("minutes", (object?)(maxSeconds / 60)) };
        return plus ? Note.Of("Over Copium Plus's limit ({size} / {minutes} min)", limits) : Note.Of("Over Copium's free limit ({size} / {minutes} min)", limits);
    }

    /// <summary>"Over Copium's free limit (200 MB / 10 min)" when the clip is longer than the account takes, else null. A smaller copy cannot help with this. Pure.</summary>
    internal static string? ShareLengthRefusalFor(double duration, bool plus)
        => duration > Capabilities.CopiumLimits(plus).Seconds ? OverLimitNote(plus).Shown : null;
    /// <summary>The clip is bigger than the account takes (a smaller copy can fix this). A clip exactly at the limit is not over. Pure.</summary>
    internal static bool ShareSizeOver(long size, bool plus) => size > Capabilities.CopiumLimits(plus).Bytes;

    /// <summary>The footer line for a clip <see cref="ShareRefusalFor"/> turned down: what the account takes, how big the clip is, what to do about it.</summary>
    internal static string ShareRefusalNotice(long size, double duration, bool plus) => ShareRefusalNoteFor(size, duration, plus).Shown;

    private static Note ShareRefusalNoteFor(long size, double duration, bool plus)
    {
        var (maxBytes, maxSeconds, _) = Capabilities.CopiumLimits(plus);
        // Rounded up: a full 10:00 replay saves a moment over 10 min (it starts on a keyframe) and must not read "10:00".
        int secs = (int)Math.Ceiling(Math.Max(0, duration));
        var args = new[]
        {
            ("limit", (object?)Note.Size(maxBytes)), ("minutes", maxSeconds / 60), ("size", Note.Size(size)),
            ("length", $"{secs / 60}:{secs % 60:00}"),
        };
        // (folder-scanned clips have no duration: no length in the sentence)
        return plus
            ? duration > 0
                ? Note.Of("Copium Plus accepts up to {limit} / {minutes} min; this clip is {size} / {length} - trim it first", args)
                : Note.Of("Copium Plus accepts up to {limit} / {minutes} min; this clip is {size} - trim it first", args)
            : duration > 0
                ? Note.Of("Copium free accepts up to {limit} / {minutes} min; this clip is {size} / {length} - trim it, or tick 'I have Copium+' if you do", args)
                : Note.Of("Copium free accepts up to {limit} / {minutes} min; this clip is {size} - trim it, or tick 'I have Copium+' if you do", args);
    }

    /// <summary>
    /// Shares a clip: copies its link when it has one, otherwise uploads it to Copium. With no Copium token there is nothing to
    /// upload to, so the share window opens with the file to drag into Discord, a browser or a folder instead
    /// (<see cref="LastShareOutcome"/> is then <see cref="ShareOutcome.FileShareOpened"/>, and the result is false without a
    /// <see cref="ShareError"/>). Returns true only when a link was copied or the clip was uploaded.
    /// </summary>
    /// <param name="allowFit">A clip over the size limit (not the length limit) is uploaded through a smaller copy made first (<see cref="MakeShareCopyAsync"/>) instead of refused; a bulk upload passes false.</param>
    /// <param name="announce">From the hotkey / tray / --share-last, which have no progress UI of their own: unless the window is in front, a toast says the upload started; and no window opens over a game.</param>
    /// <param name="declinedAfterCopy">Called once, with the reason, when this call made a smaller copy and then declined to upload it because the sign-in, plan or switches changed meanwhile (outcome <see cref="ShareOutcome.Declined"/>). Per call, so concurrent shares cannot mix their reasons up; it runs on the awaiting context, never blocks, and is not called for a prompt the user answered.</param>
    public async Task<bool> ShareAsync(ClipEntry entry, IProgress<double>? progress = null, bool announce = false, ConsentContext context = ConsentContext.ClipShare, bool quiet = false, Action? released = null, bool allowFit = true, Action<string>? declinedAfterCopy = null)
    {
        _refusal = null; RefusalKind = ShareRefusalKind.None;
        _shareError = null;
        ShareTokenRejected = false;
        LastShareOutcome = ShareOutcome.None;
        if (!string.IsNullOrEmpty(entry.CopiumLink))
        {
            // An existing link is only copied (no network), so it stays available whatever the kill switch says.
            if (!quiet)
            {
                CopyToClipboard(entry.CopiumLink);
                SayT("Link copied");
                Toasts.ShowShared(entry, entry.CopiumLink);
            }
            LastShareOutcome = ShareOutcome.LinkCopied;
            return true;
        }
        string? token = Settings.CopiumToken;
        // Sharing is opt-in and has a kill switch: cloud sharing off refuses (first, whatever else is true). With no token nothing
        // can be uploaded, so there is nothing to agree to yet: the share window (the file to drag) opens without the pop-up, which
        // comes when a token is saved or at the first real upload. With a token the first share asks and goes on only for "Enable".
        if (!Settings.CloudSharingEnabled)
        {
            _shareError = CloudOffNote;
            Say(CloudOffNote);
            Consent.Notice(context, ConsentGate.CloudOffTitleNote, CloudOffNote);
            LastShareOutcome = ShareOutcome.Declined;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(token))
        {
            // The hotkey path over a game must not put a dialog on the picture: it says where to go instead.
            if (announce && !Settings.CopiumConsent && GameOrFullScreenInFront())
            {
                var needOk = Note.Of("Sharing needs your OK once — open Clink and press Share");
                _shareError = needOk;
                Say(needOk);
                LastShareOutcome = ShareOutcome.Failed;
                return false;
            }
            if (!await EnsureCopiumConsentAsync(context))
            {
                if (!Settings.CloudSharingEnabled)
                {
                    _shareError = CloudOffNote;
                    Say(CloudOffNote);
                }
                else SayT("Not shared: sharing with Copium is not enabled");
                LastShareOutcome = ShareOutcome.Declined;
                return false;
            }
        }
        if (!Settings.CloudSharingEnabled)   // (switched off while the pop-up was up)
        {
            _shareError = CloudOffNote;
            Say(CloudOffNote);
            LastShareOutcome = ShareOutcome.Declined;
            return false;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            if (quiet)   // a bulk upload opens no window (it needs the token first)
            {
                _shareError = Note.Of("No Copium token: sign in with Copium under App → Copium");
                LastShareOutcome = ShareOutcome.Failed;
                return false;
            }
            // Nothing to upload to: hand over the file instead. The hotkey path over the game opens no window (it would land on the
            // picture): it says where to go, and the caller shows the line over the game. "Over the game" is the detected game, and
            // also a full-screen application the shell reports (a Direct3D game Clink did not detect, another full-screen program).
            if (announce && GameOrFullScreenInFront())
            {
                var noToken = Note.Of("No Copium token: open Clink to share the file");
                _shareError = noToken;
                Say(noToken);
                LastShareOutcome = ShareOutcome.Failed;
                return false;
            }
            ShareFile(entry);
            return false;
        }
        // Defence in depth: nothing is uploaded without the user's yes and with the kill switch off, whichever path got here.
        if (!Settings.CopiumConsent)
        {
            SayT("Not shared: sharing with Copium is not enabled");
            LastShareOutcome = ShareOutcome.Declined;
            return false;
        }
        // Copium refuses clips over the account's limits (200 MB / 10 min free, 2 GB / 60 min with Copium+): say so now instead of uploading for a while and failing.
        // A clip that is only over the size limit goes on (allowFit) to be uploaded through a smaller copy; the length limit, and every
        // clip of a bulk upload, still refuse here.
        bool needsCopy = allowFit && ShareSizeOver(entry.Size, Settings.CopiumPlus) && ShareLengthRefusalFor(entry.Duration, Settings.CopiumPlus) == null;
        if (!needsCopy && ShareRefusalFor(entry.Size, entry.Duration, Settings.CopiumPlus) is string refusal)
        {
            _refusal = OverLimitNote(Settings.CopiumPlus); RefusalKind = ShareRefusalKind.OverLimit;
            Say(ShareRefusalNoteFor(entry.Size, entry.Duration, Settings.CopiumPlus));
            LastShareOutcome = ShareOutcome.Refused;
            return false;
        }
        // No smaller copies here (as administrator, a redirected cache folder): known at once, so refuse before any job, mark or toast.
        if (needsCopy && !ShareCopiesAvailable)
        {
            var unavailable = ShareFitModel.UnavailableNote(ShareFitModel.ForCopium(Settings.CopiumPlus), ShareUnavailableKind.Elevated, entry.Size);
            _refusal = unavailable; RefusalKind = ShareRefusalKind.SmallerCopy;
            Say(unavailable);
            LastShareOutcome = ShareOutcome.Refused;
            return false;
        }
        // One upload per clip: the hotkey, the tray, the toast and the clip list can all ask for the same clip while it is going
        // up (nothing marks it shared until it is done). A second ask waits for the running upload and gets its result.
        if (_uploads.TryGetValue(entry, out var running))
        {
            SayT("Already uploading this clip…");
            if (announce && !MainWindowInFront) ShowUploadToast(entry, L.T("Already uploading to Copium…"));
            released?.Invoke();   // this caller's row is not the uploader: it must not keep showing the progress
            var joinedOutcome = await running.Task;
            LastShareOutcome = joinedOutcome;   // the running upload's own outcome (a refusal stays a refusal)
            return joinedOutcome == ShareOutcome.Uploaded;
        }
        bool plusAtStart = Settings.CopiumPlus;   // the plan the copy is made for: compared again after the copy
        var done = new TaskCompletionSource<ShareOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uploads[entry] = (done.Task, announce);
        bool ok = false;
        bool declined = false;   // consent, cloud sharing or the token changed during the copy: nothing is uploaded
        bool killed = false;   // stopped by the cloud kill switch: a neutral "not shared", not a failure
        bool fitRefused = false;   // no smaller copy could be made (or the clip is too long): refused, with ShareRefusal saying why
        ShareCopyResult? copy = null;   // the smaller copy being uploaded; disposed when the upload is over (it holds the cache's in-use mark)
        using var uploadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);   // this upload only: the cloud kill switch cancels it
        _uploadCancels.Add(uploadCts);
        try
        {
            // The start line and toast. For a copy they wait for the copy's first report: a refusal known at once (a game in front, no
            // room, a clip that is too long) must not be preceded by a "Making a smaller copy" that was never going to happen.
            bool toast = announce && !MainWindowInFront;   // read here: the copy's reports come from a worker thread
            var announced = new System.Runtime.CompilerServices.StrongBox<int>();
            bool Announce(Note text)   // once per upload; false when it had been said already (shown translated, logged in English)
            {
                if (Interlocked.Exchange(ref announced.Value, 1) != 0) return false;
                Say(text);
                if (toast) ShowUploadToast(entry, text.Shown);
                return true;
            }
            string title = (entry.Game ?? "Clip") + " " + entry.Created.ToString("yyyy-MM-dd HH:mm");
            string path = entry.Path;
            if (!needsCopy) Announce(Note.Of("Uploading to Copium…"));
            else
            {
                var fit = await FitForCopiumAsync(entry, progress, () => Announce(CopyingNote), uploadCts.Token);
                copy = fit.Copy;
                path = fit.Path;
                if (fit.Refusal != null)
                {
                    fitRefused = true;
                    _refusal = fit.Refusal; RefusalKind = fit.Kind;
                    Say(fit.Notice ?? fit.Refusal);
                }
                else if (!(Settings.CloudSharingEnabled && Settings.CopiumConsent && Settings.CopiumToken == token && Settings.CopiumPlus == plusAtStart))
                {
                    // The copy can take minutes: sharing was switched off, consent withdrawn, the token or the plan changed (a manual
                    // sign-out keeps the token's server side, so it is not cancelled) while it was made. Nothing is uploaded.
                    declined = true;
                    copy?.Dispose();
                    copy = null;
                    Note why;
                    if (!Settings.CloudSharingEnabled)
                    {
                        _shareError = CloudOffNote;
                        why = CloudOffNote;
                    }
                    else if (!Settings.CopiumConsent) why = Note.Of("Not shared: sharing with Copium is not enabled");
                    else if (Settings.CopiumToken != token) why = Note.Of("Not shared: the Copium sign-in changed while the copy was made");
                    else why = Note.Of("Not shared: your Copium plan changed while the copy was made. Share it again");
                    Say(why);
                    declinedAfterCopy?.Invoke(why.Shown);
                }
                else
                {
                    SetSharePhase(entry, SharePhase.Uploading);
                    progress?.Report(0);   // the row / toast / trim line goes from "Making a smaller copy..." to "Uploading..."
                    if (!Announce(Note.Of("Uploading to Copium…"))) SayT("Uploading to Copium…");   // no copy was made (it fits, or one was ready): the toast says the upload started
                }
            }
            if (!fitRefused && !declined)
            {
                // Off the UI thread: opening an archived clip on an unreachable share blocks in the FileStream constructor for ~20 s.
                // The callers' Progress<T> were made on the UI thread and still report there.
                var up = await Task.Run(() => CopiumClient.UploadAsync(token, path, title, progress, uploadCts.Token));
                entry.CopiumLink = up.Link;   // the link belongs to the original clip, whichever file went up
                entry.CopiumPageUrl = up.PageUrl;
                entry.CopiumId = up.Id;
                Library.Update(entry);
                if (!quiet)
                {
                    CopyToClipboard(up.Link);
                    SayT("Uploaded, link copied");
                    Toasts.ShowShared(entry, up.Link);
                }
                ok = true;
            }
        }
        catch (Exception ex)
        {
            _refusal = null; RefusalKind = ShareRefusalKind.None;   // another clip's Share may have been refused while this one was going up
            if (uploadCts.IsCancellationRequested && !_cts.IsCancellationRequested && !Settings.CloudSharingEnabled)
            {
                Log.Info("Copium upload: cancelled, cloud sharing was turned off");
                killed = true;
                _shareError = CloudOffNote;
                SayT("Not shared: cloud sharing is off");
            }
            else
            {
                Log.Error("Copium upload", ex);
                var failure = UserText.NoteOf(ex);   // Copium's refusals and timeouts carry a note: the row and the toast show it translated, the log (above) has the English Message
                _shareError = failure;
                ShareTokenRejected = ex is CopiumHttpException tokenEx && CopiumClient.IsTokenRejection(tokenEx.StatusCode, tokenEx.ErrorText);
                SayT("Upload failed: {error}", ("error", failure));
            }
        }
        finally
        {
            copy?.Dispose();
            ClearSharePhase(entry);
            _uploads.Remove(entry);
            _uploadCancels.Remove(uploadCts);
            var outcome = ok ? ShareOutcome.Uploaded : killed || declined ? ShareOutcome.Declined : fitRefused ? ShareOutcome.Refused : ShareOutcome.Failed;
            LastShareOutcome = outcome;   // before the waiting asks continue, which read it
            done.SetResult(outcome);
        }
        return ok;
    }

    internal static Note CopyingNote => Note.Of("Making a smaller copy for Copium…");
    internal static string CopyingToastText => CopyingNote.Shown;

    /// <summary>Test seam: receives the "upload started" toast text instead of the real toast (a unit test must not put a window on the screen).</summary>
    internal Action<ClipEntry, string>? UploadToastSink { get; set; }
    private void ShowUploadToast(ClipEntry entry, string text)
    {
        if (UploadToastSink is { } sink) sink(entry, text);
        else Toasts.ShowUploading(entry, text);   // posts to the UI thread: any thread may call
    }

    private enum SharePhase { Waiting, Copying, Uploading }
    private readonly Dictionary<string, SharePhase> _sharePhases = new(StringComparer.OrdinalIgnoreCase);   // by clip path; guarded by itself

    private void SetSharePhase(ClipEntry entry, SharePhase phase) { lock (_sharePhases) _sharePhases[entry.Path] = phase; }
    private void ClearSharePhase(ClipEntry entry) { lock (_sharePhases) _sharePhases.Remove(entry.Path); }

    /// <summary>
    /// The line a row, a toast or the trim editor shows for the progress <paramref name="p"/> (0..1) of a clip's <see cref="ShareAsync"/>:
    /// "Making a smaller copy for Copium... 40%" while the copy is made ("Waiting..." for the slot behind another copy), else "Uploading… 40%".
    /// </summary>
    internal string ShareProgressText(ClipEntry entry, double p)
    {
        SharePhase phase;
        lock (_sharePhases) phase = _sharePhases.TryGetValue(entry.Path, out var found) ? found : SharePhase.Uploading;
        return phase switch
        {
            SharePhase.Copying => ShareFitModel.WorkingText(ShareFitModel.ForCopium(Settings.CopiumPlus), null, p),
            SharePhase.Waiting => ShareFitModel.WaitingText,
            _ => UploadingPercentText(p),
        };
    }

    /// <summary>"Uploading… 40%": the line of an upload that is under way (a clip, or a screenshot of a bulk upload, which has no smaller-copy phase).</summary>
    internal static string UploadingPercentText(double p) => L.T("Uploading… {percent}", ("percent", p.ToString("P0", L.FormatCulture)));

    /// <summary>What <see cref="FitForCopiumAsync"/> found: the file to upload (and the copy to dispose afterwards), or why nothing is uploaded.</summary>
    private sealed record FitOutcome(string Path, Note? Refusal, Note? Notice, ShareCopyResult? Copy, ShareRefusalKind Kind = ShareRefusalKind.SmallerCopy);

    /// <summary>
    /// The copy's reports (any thread) for one upload: the share phase, the caller's progress, and <paramref name="first"/> once, at the
    /// first report (the copy has really started). <see cref="Close"/> after the copy is made: a late report then changes nothing, so
    /// it cannot set the phase after the switch to uploading or after the clean-up.
    /// </summary>
    private sealed class CopyReports(AppController app, ClipEntry entry, IProgress<double>? progress, Action? first) : IProgress<ShareCopyProgress>
    {
        private bool _closed, _started;   // guarded by app._sharePhases

        public void Report(ShareCopyProgress value)
        {
            bool isFirst = false;
            lock (app._sharePhases)
            {
                if (_closed) return;
                app._sharePhases[entry.Path] = value.Stage == ShareCopyStage.Waiting ? SharePhase.Waiting : SharePhase.Copying;
                if (!_started && value.Stage == ShareCopyStage.Working)   // a wait is not the copy: the job still checks the game, the disk and the plan
                {
                    isFirst = true;
                    _started = true;
                }
            }
            if (isFirst) first?.Invoke();
            progress?.Report(value.Fraction);
        }

        public void Close()
        {
            lock (app._sharePhases) _closed = true;
        }
    }

    /// <summary>
    /// A clip over Copium's size limit: reads its real length (a folder-scanned clip has none) and checks that limit, then makes the
    /// smaller copy (<see cref="MakeShareCopyAsync"/>; <paramref name="ct"/> cancels it and removes its part file). The outcome says what
    /// to upload, or the refusal: no copy fits, it could not be made, a game is in front with copies during games off, smaller copies are
    /// not available here (as administrator) and the original is too big.
    /// </summary>
    private async Task<FitOutcome> FitForCopiumAsync(ClipEntry entry, IProgress<double>? progress, Action? copyStarted, CancellationToken ct)
    {
        bool plus = Settings.CopiumPlus;
        var target = ShareFitModel.ForCopium(plus);
        double seconds = entry.Duration;
        if (seconds <= 0)
        {
            string src = entry.Path;
            double keyframe = Settings.KeyframeSeconds;
            try { seconds = await Task.Run(() => ShareTranscoderImpl.Probe(src, keyframe).DurationSeconds, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("share: could not read the clip's length: " + ex.Message); }
            if (seconds > Capabilities.CopiumLimits(plus).Seconds)
                return new FitOutcome(entry.Path, OverLimitNote(plus), ShareRefusalNoteFor(entry.Size, seconds, plus), null, ShareRefusalKind.OverLimit);
        }
        var reports = new CopyReports(this, entry, progress, copyStarted);
        ShareCopyResult result;
        try { result = await MakeShareCopyAsync(entry, target, reports, ct); }
        finally { reports.Close(); }
        if (result.Outcome == ShareCopyOutcome.UseCopy)
        {
            // A last look at what is about to go up: a copy over the limit or over the length would only be refused by Copium.
            Note? refusal = null;
            var refusalKind = ShareRefusalKind.SmallerCopy;
            if (result.Bytes > target.LimitBytes)
                refusal = ShareFitModel.FailedNote(target, ShareFitModel.StayedTooBigReason, result.OriginalBytes > 0 ? result.OriginalBytes : entry.Size, ShareCopyError.StayedTooBig);
            else if (result.Seconds > Capabilities.CopiumLimits(plus).Seconds)
            {
                refusal = OverLimitNote(plus);
                refusalKind = ShareRefusalKind.OverLimit;
            }
            if (refusal != null)
            {
                Log.Info($"Copium upload: the copy is not usable ({result.Bytes} bytes, {result.Seconds:0.#} s): {refusal.English}");
                result.Dispose();
                return new FitOutcome(entry.Path, refusal, refusal, null, refusalKind);
            }
            return new FitOutcome(result.Path, null, null, result);
        }
        if (result.Outcome == ShareCopyOutcome.UseOriginal && result.Error == null)
        {
            result.Dispose();
            return new FitOutcome(entry.Path, null, null, null);   // it fits after all
        }
        try
        {
            if (result.Outcome == ShareCopyOutcome.Cancelled)
            {
                ct.ThrowIfCancellationRequested();   // the kill switch or quitting
                throw new IOException("making the smaller copy was cancelled");   // another asker of the same copy cancelled it
            }
            long original = result.OriginalBytes > 0 ? result.OriginalBytes : entry.Size;
            var state = ShareFitModel.StateFor(result);
            Note? stateText = ShareFitModel.TextNote(state, target, original, seconds);
            Note text = stateText ?? OverLimitNote(plus);
            // Sentences (a failed or unavailable copy, a clip that can't fit) go into the trim line whole; the over-limit phrase and the
            // game-in-front phrase ("Too big for Copium: open Clink to make a smaller copy") are not sentences and get their own wording.
            var kind = stateText == null ? ShareRefusalKind.OverLimit : state.Kind == ShareFitStateKind.Refused ? ShareRefusalKind.Phrase : ShareRefusalKind.SmallerCopy;
            Log.Info($"Copium upload: no smaller copy ({result.Outcome}, {result.ErrorKind}): {text.English}");
            return new FitOutcome(entry.Path, text, text, null, kind);
        }
        finally { result.Dispose(); }
    }

    /// <summary>Test seam: the detected game, or a full-screen application, has the screen (no window or dialog may open over it).</summary>
    internal Func<bool> GameOrFullScreenProbe { get; set; } = () => Hotkeys.GameInFront() || UserNotificationState.FullScreenInFront();
    private bool GameOrFullScreenInFront() => GameOrFullScreenProbe();

    private readonly List<ShareWindow> _shareWindows = new();   // the open share windows (UI thread), one per clip

    /// <summary>Test seam: opens the share window instead of the real one (a unit test must not put a window on the screen).</summary>
    internal Func<ClipEntry, bool>? ShareWindowOpener { get; set; }

    /// <summary>
    /// Opens the share window for a clip: its file to drag into Discord, a browser upload box or a folder, Copy file, Open folder.
    /// An open window for the same clip is brought forward instead of a second one. It has no owner (unlike the trim editor): the
    /// user minimizes or hides the main window to get at the drop target, and an owned window would go with it. It floats on top
    /// by itself and opens centred on the monitor under the pointer, except over a game (<see cref="OverGame"/>): there it opens
    /// without taking the focus, at the toasts' position on the captured monitor, and goes behind the game when that has the focus.
    /// UI thread; from another it runs there. False when the window could not be opened (logged).
    /// </summary>
    public bool OpenShareWindow(ClipEntry entry)
    {
        if (ShareWindowOpener is { } opener) return opener(entry);
        if (!_ui.CheckAccess()) return _ui.Invoke(() => OpenShareWindow(entry));
        try
        {
            TestGuard.CheckLaunch("the share window");   // before a window exists or is registered
            bool overGame = OverGame;
            var open = _shareWindows.FirstOrDefault(w => string.Equals(w.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            if (open != null) { open.BringForward(activate: !overGame); return true; }
            var window = new ShareWindow(entry);
            _shareWindows.Add(window);
            window.Closed += (_, _) => _shareWindows.Remove(window);
            try
            {
                if (overGame) window.ShowOverGame(SelectedMonitor, Settings.ToastPosition);
                else window.Show();
            }
            catch { _shareWindows.Remove(window); throw; }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("share window", ex);
            return false;
        }
    }

    /// <summary>
    /// The user is over a game that Clink detected: the game has the focus, or a toast over it was just clicked (the click made the
    /// toast the active window, so the game is no longer in front; and the toast asks without <c>announce</c>, so the hotkey path's
    /// "no window over the game" never applies to it). A window that opens then must neither take the focus from the game nor sit in
    /// the middle of its picture. With no game running (the main window, the trim editor, the desktop) it is false.
    /// </summary>
    private bool OverGame => CurrentGame != null && (Hotkeys.GameInFront() || ToastIsActive);

    private static bool ToastIsActive => System.Windows.Application.Current?.Windows.OfType<ToastWindow>().Any(t => t.IsActive) == true;

    /// <summary>The share window's "Set up Copium…": brings the main window forward and says where to sign in.</summary>
    public void ShowCopiumSetup()
    {
        if (!Settings.CloudSharingEnabled) { Say(CloudOffNote); ShowRequested?.Invoke(); return; }   // there is no token box while sharing is off
        SayT("Sign in with Copium under App → Copium");
        ShowRequested?.Invoke();
        CopiumSetupRequested?.Invoke();
    }

    /// <summary>Raised after <see cref="ShowRequested"/> by <see cref="ShowCopiumSetup"/>: the window selects the App tab and focuses the Copium card's sign-in button (or its token box).</summary>
    public event Action? CopiumSetupRequested;

    /// <summary>Asks the window to open the trim editor on a clip. False when no window has subscribed to <see cref="TrimRequested"/>.</summary>
    public bool RequestTrim(ClipEntry entry)
    {
        var h = TrimRequested;
        if (h == null) return false;
        _ui.BeginInvoke(() => h(entry));
        return true;
    }

    /// <summary>Test seam: receives the text instead of the real clipboard.</summary>
    internal Action<string>? ClipboardOverride { get; set; }

    public void CopyToClipboard(string text) => TryCopyToClipboard(text);

    /// <summary>Puts the text on the clipboard; false (logged) when that failed.</summary>
    public bool TryCopyToClipboard(string text)
    {
        if (ClipboardOverride is { } over)
        {
            try { over(text); return true; }
            catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); return false; }
        }
        try { _ui.Invoke(() => Clipboard.SetDataObject(text, true)); return true; }
        catch (Exception ex) { Log.Warn("clipboard: " + ex.Message); return false; }
    }

    public void OpenFolder(string? file = null)
    {
        try
        {
            if (file != null && File.Exists(file)) TestGuard.Start("explorer.exe", $"/select,\"{file}\"");
            else { TestGuard.CheckLaunch("opening the clips folder"); Directory.CreateDirectory(Settings.ClipsFolder); TestGuard.Start("explorer.exe", $"\"{Settings.ClipsFolder}\""); }
        }
        catch (Exception ex) { SayT("Could not open folder: {error}", ("error", ex.Message)); }
    }

    public void SaveSettings() => Settings.Save();

    public void SetRunAtStartup(bool on)
    {
        if (IsDev) return;   // a --dev instance must never touch the real Run entry (it would point the real startup at the dev build)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("Clink", $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue("Clink", false);
        }
        catch (Exception ex) { SayT("Startup setting failed: {error}", ("error", ex.Message)); }
    }

    // ------------------------------------------------------------------ status dot (docs/SPEC-v2.md §7)

    /// <summary>Shows or hides the status dot per <see cref="Settings.StatusDot"/> / <see cref="Settings.StatusDotPosition"/> on the captured monitor. UI thread.</summary>
    public void ApplyStatusDot()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(ApplyStatusDot); return; }
        try
        {
            if (!Settings.StatusDot || !Settings.ClipsEnabled) { _dot?.Hide(); return; }
            _dot ??= new StatusDotWindow();
            _dot.Apply(State, IsManualRecording);
            _dot.Place(SelectedMonitor, Settings.StatusDotPosition);
        }
        catch (Exception ex) { Log.Warn("status dot: " + ex.Message); }
    }

    // ------------------------------------------------------------------ tray

    /// <summary>The tray icon's name: "Clink", or "TEST · Clink (dev)" for a --dev instance, so the two icons can be told apart.</summary>
    private static string TrayName => IsDev ? DevInstanceMarker.TrayText("Clink (dev)") : "Clink";

    private void CreateTray()
    {
        try
        {
            var menu = BuildTrayMenu();
            _trayIcon = LoadTrayIcon();
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = TrayName,
                Visible = true,
                ContextMenuStrip = menu,
                Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            };
            _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowRequested?.Invoke(); };
            ApplyTrayVisibility();
        }
        catch (Exception ex) { Log.Warn("tray: " + ex.Message); }
    }

    /// <summary>The tray's right-click menu, built and wired but not shown (the unit tests read its texts). Sets the item fields.</summary>
    internal System.Windows.Forms.ContextMenuStrip BuildTrayMenu()
    {
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            if (L.IsRtl) menu.RightToLeft = System.Windows.Forms.RightToLeft.Yes;   // a right-to-left language mirrors the menu; English leaves the default
            _statusItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Starting…")) { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _clipItem = new System.Windows.Forms.ToolStripMenuItem(ClipTrayText, null, (_, _) => _ui.BeginInvoke(() => ClipNow()));
            menu.Items.Add(_clipItem);
            _screenshotItem = new System.Windows.Forms.ToolStripMenuItem(ScreenshotTrayText, null, (_, _) => _ui.BeginInvoke(() => Screenshots.CaptureRegion(Clink.Screenshots.ScreenshotTrigger.Tray)))
                { Visible = Settings.ScreenshotsEnabled };
            menu.Items.Add(_screenshotItem);
            _screenshotEditItem = new System.Windows.Forms.ToolStripMenuItem(ScreenshotEditTrayText, null, (_, _) => _ui.BeginInvoke(() => Screenshots.EditLast()))
                { Visible = Settings.ScreenshotsEnabled };
            menu.Items.Add(_screenshotEditItem);
            _recordItem = new System.Windows.Forms.ToolStripMenuItem(RecordItemText, null, (_, _) => _ui.BeginInvoke(ToggleRecording));
            menu.Items.Add(_recordItem);
            _shareLastItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Share last clip"), null, (_, _) => _ui.BeginInvoke(ShareLast));
            menu.Items.Add(_shareLastItem);
            menu.Items.Add(L.T("Show Clink"), null, (_, _) => ShowRequested?.Invoke());
            _pauseItem = new System.Windows.Forms.ToolStripMenuItem(PauseItemText, null, (_, _) => TogglePause());
            menu.Items.Add(_pauseItem);
            _openFolderItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Open clips folder"), null, (_, _) => OpenFolder());
            menu.Items.Add(_openFolderItem);
            _updateItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Update Clink"), null, (_, _) => UpdateNow()) { Visible = false };
            menu.Items.Add(_updateItem);
            _importItem = new System.Windows.Forms.ToolStripMenuItem(L.T("Import settings…"), null, (_, _) => _ui.BeginInvoke(AskToImport)) { Visible = false };
            menu.Items.Add(_importItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(L.T("tray|Quit"), null, (_, _) => Quit());
            return menu;
        }
    }

    /// <summary>
    /// The exe's icon frame that matches the tray's small-icon size at the taskbar's DPI (16 / 20 / 24 / 32 px at 100 / 125 / 150 / 200 %),
    /// so the shell shows a frame that was drawn for that size instead of shrinking the 32 px one (which is what
    /// Icon.ExtractAssociatedIcon returns). The caller owns the result; null if no icon could be read from the exe.
    /// </summary>
    private static System.Drawing.Icon? LoadTrayIcon()
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return null;
        try
        {
            int size = AppIcons.SmallIconSize();
            if (size > 0)
            {
                var icon = System.Drawing.Icon.ExtractIcon(exe, 0, size);
                if (icon != null) return icon;
            }
        }
        catch (Exception ex) { Log.Warn("tray icon size: " + ex.Message); }
        try { return System.Drawing.Icon.ExtractAssociatedIcon(exe); }
        catch (Exception ex) { Log.Warn("tray icon: " + ex.Message); return null; }
    }

    /// <summary>Swaps in the icon frame for the current DPI (after a display / scaling change) and frees the old one.</summary>
    private void RefreshTrayIcon()
    {
        if (_tray == null || _disposed) return;
        try
        {
            var icon = LoadTrayIcon();
            if (icon == null) return;
            if (_trayIcon != null && _trayIcon.Width == icon.Width) { icon.Dispose(); return; }   // same size: leave the icon alone
            var old = _trayIcon;
            _tray.Icon = icon;
            _trayIcon = icon;
            old?.Dispose();
        }
        catch (Exception ex) { Log.Warn("tray icon refresh: " + ex.Message); }
    }

    private void UpdateTray()
    {
        if (_tray == null) return;
        string status = StatusText;
        status = TrayStatusFor(status, State, Engine.ErrorInfo);
        string text = TrayText(TrayName, status);
        if (text != _trayText)
        {
            _trayText = text;
            try { _tray.Text = RtlFirst(text); } catch { }
            if (_statusItem != null) _statusItem.Text = status;
        }
    }

    /// <summary>The tray's status line: an engine error in its own words while the state is Error, otherwise the status as it is.</summary>
    internal static string TrayStatusFor(string status, RecordingState state, EngineError? engineError) =>
        state == RecordingState.Error && engineError != null ? L.T("Error: {error}", ("error", EngineStatusText.Error(engineError))) : status;

    /// <summary>The tray icon's tooltip: the name, a dot and the status, cut to 120 characters (NotifyIcon.Text throws above 127) whatever the language.</summary>
    internal static string TrayText(string name, string status)
    {
        string text = name + " · " + status;
        if (text.Length <= 120) return text;
        int cut = 119;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;   // never split a surrogate pair
        string head = text[..cut];
        // never leave a left-to-right embedding (L.Ltr's LRE ... PDF) open: close what the cut left open (an LRE costs one PDF, at most a few)
        int open = 0;
        foreach (char c in head)
        {
            if (c is '\u202A' or '\u202B' or '\u202D' or '\u202E') open++;
            else if (c == '\u202C' && open > 0) open--;
        }
        return head + "…" + new string('\u202C', Math.Min(open, 6));
    }

    public void UpdateTrayHotkey()
    {
        if (_clipItem != null) _clipItem.Text = ClipTrayText;
        if (_screenshotItem != null) _screenshotItem.Text = ScreenshotTrayText;
        if (_screenshotEditItem != null) _screenshotEditItem.Text = ScreenshotEditTrayText;
        ApplyTrayVisibility();
    }

    /// <summary>
    /// Which tray items show, from the features and the kill switch: the clip, record, pause, open-folder items and the status line
    /// need Clips; "Share last clip" needs Clips and cloud sharing; the screenshot items need Screenshots.
    /// </summary>
    internal static (bool Clips, bool Share, bool Screenshots) TrayVisibility(Settings s) =>
        (s.ClipsEnabled, s.ClipsEnabled && s.CloudSharingEnabled, s.ScreenshotsEnabled);

    private void ApplyTrayVisibility()
    {
        var (clips, share, screenshots) = TrayVisibility(Settings);
        if (_statusItem != null) _statusItem.Visible = clips;
        if (_clipItem != null) _clipItem.Visible = clips;
        if (_recordItem != null) _recordItem.Visible = clips;
        if (_pauseItem != null) _pauseItem.Visible = clips;
        if (_openFolderItem != null) _openFolderItem.Visible = clips;
        if (_shareLastItem != null) _shareLastItem.Visible = share;
        if (_screenshotItem != null) _screenshotItem.Visible = screenshots;
        if (_screenshotEditItem != null) _screenshotEditItem.Visible = screenshots;
    }

    /// <summary>
    /// Under a right-to-left language, a right-to-left mark in front of text the shell draws (a tray tooltip, a balloon): Win32 takes the
    /// direction of the first strong character, and a translation that starts with a left-to-right piece (a hotkey, a path, a name) would lay out left to right.
    /// </summary>
    internal static string RtlFirst(string text) => L.IsRtl && text.Length > 0 ? "\u200F" + text : text;

    public void ShowBalloon(string title, string text)
    {
        if (IsQuiet) return;   // --dev-quiet: no tray balloon
        if (text.Length > 250) text = text[..249] + "…";   // Shell_NotifyIcon cuts balloon text at 256 characters
        title = RtlFirst(title);
        text = RtlFirst(text);
        if (TestGuard.Active && _tray != null) TestGuard.CheckLaunch("a tray balloon: " + title);   // test mode only: nothing is shown
        try { _tray?.ShowBalloonTip(3000, title, text, System.Windows.Forms.ToolTipIcon.None); } catch { }
    }

    /// <param name="userInitiated">The tray's Quit / the menu: an open screenshot editor with unsaved edits asks and can cancel the quit. An update restart is not (the new version is launched already): the edits are saved as a copy.</param>
    public void Quit(bool userInitiated = true)
    {
        _ui.BeginInvoke(() => QuitAsync(userInitiated));
    }

    private bool _quitAsking;
    private bool _forcedQuitPending;   // an update restart asked to quit while the user's question was up: it cannot be refused

    private async void QuitAsync(bool userInitiated)
    {
        if (_quitAsking)   // the editor's question is up already
        {
            if (!userInitiated) _forcedQuitPending = true;   // remembered: Cancel then no longer keeps Clink running
            return;
        }
        bool go = true;
        if (_screenshotEditor is { IsOpen: true } editor)
        {
            _quitAsking = true;
            try
            {
                go = await editor.ConfirmQuitAsync(userInitiated);
                if (!go && _forcedQuitPending)
                {
                    Log.Info("quit: a forced quit came in during the question: saving a copy and quitting");
                    go = await editor.ConfirmQuitAsync(userInitiated: false);
                }
            }
            catch (Exception ex) { Log.Error("quit: screenshot editor", ex); }
            finally { _quitAsking = false; _forcedQuitPending = false; }
        }
        if (!go)
        {
            Log.Info("Quit cancelled from the screenshot editor");
            return;
        }
        // A throw while cleaning up must not leave a process with no tray icon running on.
        try { StopRecordingForExit(); Dispose(); }
        finally { System.Windows.Application.Current.Shutdown(); }
    }

    /// <summary>Windows is ending the session (cannot be refused): unsaved screenshot edits are saved as a copy, waiting at most ~3 s with the UI still pumping.</summary>
    internal void SaveEditorForSessionEnd()
    {
        // Follow-up (accepted for S5): edits made in the capture overlay (mode "before") are NOT saved here; they are lost on a quit / session end.
        if (_screenshotEditor is not { IsOpen: true } editor) return;
        try
        {
            var task = editor.ConfirmQuitAsync(userInitiated: false);
            var until = DateTime.UtcNow.AddSeconds(3);
            while (!task.IsCompleted && DateTime.UtcNow < until)
            {
                var frame = new DispatcherFrame();
                _ui.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
            if (!task.IsCompleted) Log.Warn("session end: the screenshot editor's save did not finish within 3 s");
        }
        catch (Exception ex) { Log.Error("session end: screenshot editor", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;   // Quit disposes, then App.OnExit does again
        _disposed = true;
        Log.Info("Quitting");
        _cts.Cancel();
        try { WaitForSaves(ExitStopLimit); } catch (Exception ex) { Log.Warn("Exit: saves: " + ex.Message); }   // a running "Save a copy" removes its .clinkpart within 4 s
        try { WaitForShareCopies(ExitStopLimit); } catch (Exception ex) { Log.Warn("Exit: share copies: " + ex.Message); }   // a running smaller copy deletes its unfinished file within 4 s
        try { _signIn?.Shutdown(); } catch (Exception ex) { Log.Warn("Exit: sign-in: " + ex.Message); }   // no network wait: a late approval is simply never stored
        _timer?.Stop();
        CopiumClient.UploadTooLarge -= OnUploadTooLarge;
        SettingsImport.PendingChanged -= OnImportPending;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { Hotkeys.Uninstall(); } catch { }
        try { Archive.Stop(); } catch { }
        try { ShareCache.DisposeCurrent(); } catch { }
        try { _screenshots?.CancelUploads(ExitStopLimit); } catch (Exception ex) { Log.Warn("Exit: screenshot uploads: " + ex.Message); }   // a running upload ends within 4 s (the HTTP call is cancelled)
        try { Toasts.CloseAll(); } catch { }
        try { _dot?.Close(); _dot = null; } catch { }
        try { Updater.Dispose(); } catch { }
        try { Engine.Dispose(); } catch { }
        AddFinishedRecordingOnExit();   // the recording Dispose finished (the 15 s wait): the exit paths that skipped StopRecordingForExit did not add it
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        try { _trayIcon?.Dispose(); _trayIcon = null; } catch { }
        ReleaseSingleInstance();
    }
}
