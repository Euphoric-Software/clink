using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Clink.Services;

/// <summary>
/// A newer release (or, on the dev channel, dev build) found in the signed feed. <see cref="Sha256"/> (lowercase hex), <see cref="ExeSize"/> and
/// <see cref="ExeUrl"/> come from a verified signed manifest and the download fetches no hash file. <see cref="NotesUrl"/> is the manifest's
/// <c>notesUrl</c> as signed, which is opened only after <see cref="Updater.ReleasePageFor"/> has checked it. <see cref="Flavour"/> is the
/// flavour of the asset on offer (a framework client that falls back to the self-contained exe gets <see cref="Packaging.SelfContained"/> here).
/// </summary>
internal sealed record UpdateInfo(ReleaseVersion Version, string Tag, string ExeUrl, long ExeSize, string Sha256, string NotesUrl, Packaging Flavour = Packaging.SelfContained);

/// <summary>
/// A problem of a check pass worth one notification outside the window (<see cref="Updater.CheckFinished"/>): the download attempt cap of a
/// version was reached, or the checks have been failing for days. <see cref="Key"/> names the version or the channel, so the
/// same problem is announced once per session however many passes report it (<see cref="UpdateNoticeGate"/>). Title and text fit a tray balloon.
/// </summary>
internal sealed record UpdateNotice(string Key, string Title, string Text);

/// <summary>Session de-duplication of <see cref="UpdateNotice"/>s: each key is let through once per process (thread-safe).</summary>
internal sealed class UpdateNoticeGate
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>True the first time a notice with this key is offered, false for null and for every later one.</summary>
    public bool ShouldShow(UpdateNotice? notice)
    {
        if (notice == null) return false;
        lock (_lock) return _shown.Add(notice.Key);
    }
}

/// <summary>
/// Self-update from our own signed static feed (a Cloudflare R2 bucket) and from nothing else: the client never contacts any other
/// host (the source repository is not public). Checks the signed <c>stable.json</c> (or, with <see cref="Settings.UseDevBuilds"/>, also
/// <c>dev.json</c>) for a newer <c>Clink.exe</c> (<see cref="ShouldInstall(ReleaseVersion, ReleaseVersion, bool, bool)"/> says what counts
/// as newer), downloads and verifies it into <c>%LOCALAPPDATA%\Clink\updates</c>, and swaps it over the running exe either on the next
/// start (<see cref="TryGetPendingUpdate"/>) or right away (<see cref="LaunchApply"/>). The swap itself is done by the downloaded exe
/// running with <c>--apply-update</c> (<see cref="RunApplyUpdate"/>), so the old file is never written while it is still executing.
/// <para>
/// Trust (<see cref="UpdateFeed"/>, anchors in the embedded feed.json: <see cref="FeedTrust"/>): the exe's size and SHA-256 come only
/// from a manifest signed by one of the channel's built-in keys; the newest valid signed manifest is trusted as it is (no timestamp or
/// freshness service), and the version floors refuse a replayed lower version. Only a download made from a signed manifest is installed
/// or reused. It must also report the version its manifest promises. The verified hash is remembered in settings and checked again
/// before the file is installed. A build whose feed.json is empty has no updates at all (<see cref="UpdatesEnabled"/>), and a broken one
/// (<see cref="FeedState.Misconfigured"/>) turns them off with a loud log line. settings.json's UpdateRepo is an ignored legacy key.
/// </para>
/// </summary>
internal sealed class Updater : IDisposable
{
    private const int MaxDownloadAttempts = 3;              // per version, per process; a broken release should not be re-fetched every cycle
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan LoopTick = TimeSpan.FromMinutes(1);       // how soon the background loop notices AutoUpdate being switched
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);   // no bytes for this long = the download is stalled
    /// <summary>How long a pooled connection of the update clients is reused: a session that lasts days picks up DNS changes.</summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(15);

    private readonly Settings _settings;
    private readonly CancellationTokenSource _cts = new();
    internal readonly SemaphoreSlim _gate = new(1, 1);       // one check/download at a time (internal: the unit tests hold it)
    private readonly SemaphoreSlim _wake = new(0, 1);        // released by Wake(): cuts the background loop's wait short
    private readonly Dictionary<ReleaseVersion, int> _attempts = new();
    private readonly object _pendingLock = new();            // PendingPath/PendingVersion: set by the check, dropped by the UI too
    private string? _lastDownloadError;
    private Task? _loop;
    /// <summary>Set by <see cref="TrackFailures"/> when this check is the one that finds the background checks failing for days (reset by every check): its notification.</summary>
    private UpdateNotice? _failNotice;

    /// <summary>Version of the running exe, always three components (major.minor.build).</summary>
    public Version CurrentVersion => Current.Number;
    /// <summary>Version of the running exe including any pre-release suffix ("0.3.1-dev.57").</summary>
    public ReleaseVersion Current { get; }
    /// <summary>Pre-release suffix of the running exe ("beta.1" for 0.2.0-beta.1, "dev.57" for a dev build), null for a release build.</summary>
    public string? CurrentPrerelease => Current.Prerelease;
    public bool IsPrerelease => Current.IsPrerelease;
    /// <summary>"0.2.0" or "0.2.0-beta.1": what to show the user.</summary>
    public string VersionDisplay => Current.ToString();
    /// <summary>Path of a downloaded, verified newer exe (null until <see cref="UpdateReady"/> fired).</summary>
    public string? PendingPath { get; private set; }
    public ReleaseVersion? PendingVersion { get; private set; }
    /// <summary>Why the last check returned nothing or could not download what it found, or null if nothing went wrong.</summary>
    public string? LastError { get; private set; }
    /// <summary>True when <see cref="LastError"/> is about the check itself; false when the check worked and downloading or installing what it found did not.</summary>
    public bool CheckFailed { get; private set; }
    /// <summary>True while <see cref="DownloadAsync"/> is transferring the exe (a manual check waits for it).</summary>
    public bool IsDownloading { get; private set; }
    /// <summary>Set by <see cref="TryGetPendingUpdate"/> when a downloaded update could not be installed: a message for the user, or null.</summary>
    public string? InstallProblem { get; private set; }

    /// <summary>A newer release exists (raised on a background thread, before any download).</summary>
    public event Action<UpdateInfo>? UpdateAvailable;
    /// <summary>A newer exe is downloaded and verified (raised on a background thread).</summary>
    public event Action<ReleaseVersion>? UpdateReady;
    /// <summary><see cref="PendingPath"/> was dropped because the channel or the running exe no longer wants it (raised on any thread).</summary>
    public event Action? PendingCleared;
    /// <summary>
    /// A check pass ended (every one: the background loop's and a manual check's, whatever its result; not one cut short by shutdown).
    /// Raised on a background thread after the updater's gate is released, with the pass's problem worth a notification (or null), so the
    /// UI can show <see cref="LastError"/> of a background pass too.
    /// </summary>
    public event Action<UpdateNotice?>? CheckFinished;

    public Updater(Settings settings) : this(settings, GetCurrentSemVer()) { }

    /// <summary>With the running version given (the unit tests pretend to be a particular build).</summary>
    internal Updater(Settings settings, ReleaseVersion current)
    {
        _settings = settings;
        Current = current;
        // The window shows this in the Updates card ("Last check failed: updates are off in dev mode"), as the status of a --dev instance.
        if (AppController.IsDev) { LastError = DevModeNote.ToLowerInvariant(); CheckFailed = true; }
        else if (Trust.Misconfigured) { LastError = MisconfiguredNote; CheckFailed = true; }   // and UpdatesEnabled is false: nothing is checked or installed
    }

    /// <summary>What the updater says (<see cref="LastError"/>) when the embedded feed.json is broken (<see cref="FeedState.Misconfigured"/>): it checks nothing at all.</summary>
    public const string MisconfiguredNote = "update configuration invalid: this build's signed update settings (feed.json) are broken, so it does not check for or install updates (details in the log)";

    /// <summary>
    /// Where updates are downloaded instead of <c>%LOCALAPPDATA%\Clink\updates</c>, the folder shared by every Clink exe of this user
    /// (installed, standalone or portable alike: <see cref="AppLayout.UpdatesDir"/> is the same path). Only the unit tests set it (the
    /// runner points it at a per-run scratch folder); the app never does, so it is null in a real run.
    /// </summary>
    internal static string? UpdatesDirOverride { get; set; }

    /// <summary>Stands in for the detected <see cref="PackagingInfo.Current"/> in the unit tests (the runner pins SelfContained); null otherwise.</summary>
    internal static Packaging? PackagingOverride { get; set; }
    /// <summary>The packaging of this copy: decides the update asset, the updates folder and whose pending download it may touch.</summary>
    public static Packaging OwnPackaging => PackagingOverride ?? PackagingInfo.Current.Packaging;

    /// <summary>The base updates folder shared by every Clink exe of this user: SelfContained copies use it directly, Framework copies its <c>fx</c> subfolder (<see cref="UpdatesDirFor"/>).</summary>
    private static string UpdatesRoot => UpdatesDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink", "updates");
    /// <summary>Updates folder of <paramref name="packaging"/>: <c>updates</c> (SelfContained, as ever) or <c>updates\fx</c> (Framework). A copy never looks into the other one.</summary>
    internal static string UpdatesDirFor(Packaging packaging) => packaging == Packaging.Framework ? Path.Combine(UpdatesRoot, "fx") : UpdatesRoot;
    public static string UpdatesDir => UpdatesDirFor(OwnPackaging);

    /// <summary>What a --dev instance says when asked to look for, download or install an update.</summary>
    public const string DevModeNote = "Updates are off in dev mode";

    /// <summary>
    /// True when this instance may check for, download and install updates: the embedded feed.json is a valid, complete one
    /// (<see cref="FeedTrust.On"/>) and this is not a <c>--dev</c> instance (it would download into the real Clink's updates folder,
    /// delete the other exes there, the real Clink's pending update among them, and restart as a normal Clink). False for a build
    /// whose feed.json is empty (no updates at all, nothing is ever contacted) and for a broken one (<see cref="FeedState.Misconfigured"/>).
    /// settings.json's <c>UpdateRepo</c> is an ignored legacy key and changes nothing.
    /// </summary>
    public bool UpdatesEnabled => !AppController.IsDev && Trust.On;

    private static Assembly EntryAssembly => Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly;

    /// <summary>Replaces the network for the feed and the exe download (every request the updater makes): set by the unit tests, null otherwise.</summary>
    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HttpOverride { get; set; }
    /// <summary>Stands in for the embedded feed.json in the unit tests; null otherwise.</summary>
    internal static FeedTrust? FeedOverride { get; set; }
    /// <summary>Replaces the per-request timeout of the feed and signed-manifest requests (<see cref="UpdateFeed.RequestTimeout"/>) in the unit tests; null otherwise.</summary>
    internal static TimeSpan? FeedTimeoutOverride { get; set; }

    private static readonly Lazy<FeedTrust> s_embeddedTrust = new(FeedTrust.LoadEmbedded);
    /// <summary>The signed feed's trust anchors: the embedded feed.json (read once), or <see cref="FeedOverride"/>.</summary>
    internal static FeedTrust Trust => FeedOverride ?? s_embeddedTrust.Value;

    /// <summary>
    /// The only HTTP client of the updater, for the signed feed's own host (manifest, signature and feed binaries): it never follows a
    /// redirect, so a feed that answers 3xx is unreachable (the feed is static files that do not redirect; a redirect would take a
    /// request, and the binaries' "under the base" check, somewhere the keys never vouched for).
    /// </summary>
    internal static readonly SocketsHttpHandler FeedHandler = new() { AllowAutoRedirect = false, PooledConnectionLifetime = ConnectionLifetime };
    /// <summary>
    /// 30 s: with <see cref="HttpCompletionOption.ResponseHeadersRead"/> (every request here) that bounds connecting and
    /// the headers; the bodies have their own limits (the feed's 10 s total in <see cref="UpdateFeed.FetchCappedAsync"/>, the exe's
    /// per-read deadline in <see cref="DownloadAsync"/>).
    /// </summary>
    private static readonly HttpClient s_feedHttp = new(FeedHandler) { Timeout = TimeSpan.FromSeconds(30) };
    /// <summary>The timeout of <see cref="s_feedHttp"/> (asserted by the unit tests).</summary>
    internal static TimeSpan FeedClientTimeout => s_feedHttp.Timeout;

    /// <summary>Told the URL of every request, before it is sent (the unit tests see what would be asked, whether or not HttpOverride answers it). Null otherwise.</summary>
    internal static Action<string>? SendObserver { get; set; }

    private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        SendObserver?.Invoke(req.RequestUri?.AbsoluteUri ?? "");
        return HttpOverride != null ? HttpOverride(req, ct) : s_feedHttp.SendAsync(req, option, ct);
    }

    /// <summary>Clamp of the size an exe download's total deadline is computed from (the exe is about 30-150 MB).</summary>
    internal const long MaxExeBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Replaces, in the unit tests, the 30 s the exe download waits for the server's answer (connect + headers; the clients' own Timeout
    /// does the same in a real run, but a test's HttpOverride bypasses the clients); null otherwise.
    /// </summary>
    internal static TimeSpan? DownloadAnswerTimeoutOverride { get; set; }
    private static TimeSpan DownloadAnswerTimeout => DownloadAnswerTimeoutOverride ?? TimeSpan.FromSeconds(30);
    /// <summary>Replaces the 60 s per-read inactivity deadline of the exe download (<see cref="ReadTimeout"/>) in the unit tests; null otherwise.</summary>
    internal static TimeSpan? DownloadReadTimeoutOverride { get; set; }
    private static TimeSpan DownloadReadTimeout => DownloadReadTimeoutOverride ?? ReadTimeout;

    /// <summary>The least a download must be allowed in total, whatever its size (see <see cref="DownloadDeadline"/>).</summary>
    private static readonly TimeSpan MinTotalDeadline = TimeSpan.FromMinutes(10);
    /// <summary>The slowest transfer the total deadline of a download assumes (64 KB/s): a 150 MB exe gets 40 minutes.</summary>
    private const long DeadlineBytesPerSecond = 64 * 1024;
    /// <summary>The slowest average a download may have over <see cref="RateWindow"/> once that much time has passed (16 KB/s).</summary>
    private const long MinDownloadBytesPerSecond = 16 * 1024;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);
    /// <summary>The least time between two samples of the rate window: it bounds the samples kept to about window / spacing + 1.</summary>
    private static readonly TimeSpan RateSampleSpacing = TimeSpan.FromSeconds(1);
    /// <summary>The most rate-window samples the last <see cref="DownloadAsync"/> of this updater held at once (asserted by the unit tests).</summary>
    internal int RateSamplesPeak { get; private set; }
    /// <summary>Replaces the whole total deadline of the exe download (<see cref="DownloadDeadline"/>) in the unit tests; null otherwise.</summary>
    internal static TimeSpan? DownloadTotalTimeoutOverride { get; set; }
    /// <summary>Replaces the 16 KB/s minimum average rate (bytes per second) in the unit tests; null otherwise.</summary>
    internal static long? DownloadMinRateOverride { get; set; }
    /// <summary>Replaces the 60 s window the minimum rate is averaged over in the unit tests; null otherwise.</summary>
    internal static TimeSpan? DownloadRateWindowOverride { get; set; }

    /// <summary>
    /// How long the body of an exe download of <paramref name="size"/> bytes may take in total: the larger of 10 minutes and size at 64 KB/s.
    /// The per-read inactivity limit alone lets a server that sends one byte every 59 s hold the updater's gate for ever. The size is
    /// clamped to <see cref="MaxExeBytes"/> (a huge announced or listed size must not buy a deadline of days).
    /// </summary>
    /// <remarks>
    /// Accepted for now: for a large file (over 10 minutes' worth at 64 KB/s, about 37.5 MB) this makes 64 KB/s the effective average floor
    /// of the whole transfer (size / 64 KB/s), well above the 16 KB/s minimum over the rate window, and there is no resume: a connection that
    /// slow fails the attempt and the next one starts from the first byte again.
    /// </remarks>
    internal static TimeSpan DownloadDeadline(long size) =>
        DownloadTotalTimeoutOverride ?? TimeSpan.FromSeconds(Math.Max(MinTotalDeadline.TotalSeconds, Math.Min(size, MaxExeBytes) / (double)DeadlineBytesPerSecond));

    /// <summary>The clock of the update logic (UTC); the unit tests replace it.</summary>
    internal static Func<DateTimeOffset>? ClockOverride { get; set; }
    private static DateTimeOffset Now => ClockOverride?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>A GET on the signed feed's host: the response with its headers read, the body still to be read (capped by <see cref="UpdateFeed.FetchCappedAsync"/>). No redirects (see <see cref="s_feedHttp"/>).</summary>
    private async Task<HttpResponseMessage> GetFeedAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);   // a GET has no content: disposing it leaves the response's body readable
        AddHeaders(req);
        return await SendAsync(req, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
    }

    /// <summary>
    /// Logs once at startup where updates come from and the signed feed's trust anchors in one line (an error when feed.json is
    /// broken; key counts only, never key bytes).
    /// </summary>
    public void LogSource()
    {
        var trust = Trust;
        if (trust.Misconfigured)
        {
            Log.Error("updater: " + trust.Describe());
            Log.Error("updater: UPDATES ARE OFF: " + MisconfiguredNote);
            return;
        }
        Log.Info("updater: " + trust.Describe());
        var pk = PackagingInfo.Current;
        Log.Info($"updater: packaging {OwnPackaging} (runtime says {pk.RuntimeSays}, build label {pk.BuildLabel ?? "none"}); updates folder {UpdatesDir}");
        if (pk.Disagreement != null) Log.Warn("updater: packaging: " + pk.Disagreement);
        if (AppController.IsDev) { Log.Info("updater: off in a --dev instance"); return; }
        if (!trust.On) { Log.Info("updater: no signed update feed in this build, updates disabled"); return; }
        string devNote = trust.DevFeedUrl == trust.FeedUrl ? "" : $" (dev builds from {trust.DevFeedUrl})";
        Log.Info($"updater: updates come from the signed feed {trust.FeedUrl}{devNote} and nowhere else");
        foreach (string channel in new[] { "stable", "dev" })
        {
            if (SeedProblem(channel) is { } bad) Log.Warn("updater: " + bad);
            else if (FloorSeed(channel) is { } seed) Log.Info($"updater: built-in {channel} floor v{seed} (no older {channel} manifest is accepted)");
        }
        if (!string.IsNullOrWhiteSpace(_settings.UpdateRepo)) Log.Info("updater: ignoring the legacy UpdateRepo key in settings.json");
    }

    // ------------------------------------------------------------------ versions

    private static ReleaseVersion GetCurrentSemVer()
    {
        var asm = EntryAssembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var asmVersion = asm.GetName().Version ?? new Version(0, 0, 0);
        return ReleaseVersion.Parse(info) ?? new ReleaseVersion(new Version(Math.Max(0, asmVersion.Major), Math.Max(0, asmVersion.Minor), Math.Max(0, asmVersion.Build)), null);
    }

    /// <summary>
    /// True if <paramref name="target"/> should replace the running <paramref name="current"/>. A dev build is never installed on
    /// the stable channel. Otherwise anything newer wins (a release beats the dev builds of its own number, a dev build beats the
    /// release before it). Leaving a dev build for an older release is a deliberate act: only with <paramref name="allowLeaveDev"/>
    /// (a manual check, or a download that was verified earlier) does the stable channel go back to a release that sorts lower.
    /// </summary>
    internal static bool ShouldInstall(ReleaseVersion target, ReleaseVersion current, bool devChannel, bool allowLeaveDev)
    {
        if (!devChannel && target.IsDevBuild) return false;
        if (target.IsPrerelease && !target.IsDevBuild) return false;   // only releases and X.Y.Z-dev.N are ever installed ("9.9.9-beta" never is)
        if (target > current) return true;
        return !devChannel && current.IsDevBuild && !target.IsPrerelease && allowLeaveDev;
    }

    /// <summary><see cref="ShouldInstall(ReleaseVersion, ReleaseVersion, bool, bool)"/> for the running exe and the channel chosen in settings.</summary>
    private bool ShouldInstall(ReleaseVersion target, bool allowLeaveDev) => ShouldInstall(target, Current, _settings.UseDevBuilds, allowLeaveDev);

    /// <summary>
    /// allowLeaveDev for what is found in the updates folder at startup (<see cref="TryGetPendingUpdate"/>, <see cref="CleanupInstalled"/>):
    /// only a download this very exe made (from a manual check or a channel switch) may take a dev build back to an older release,
    /// never one an older exe left behind (nor any other file than the recorded download).
    /// </summary>
    private bool LeaveDevAllowedAtStart(ReleaseVersion file) =>
        string.Equals(RecFrom, Current.ToString(), StringComparison.Ordinal)
        && string.Equals(RecVersion, file.ToString(), StringComparison.Ordinal);

    // The download record of this copy's packaging: SelfContained uses the legacy PendingUpdate* keys (they describe the root updates\ folder,
    // which v0.4.1 and dev.10 write too), Framework the PendingUpdateFx* keys (updates\fx\). Every read and write of the record goes through here.
    private bool OwnIsFx => OwnPackaging == Packaging.Framework;
    private string? RecVersion { get => OwnIsFx ? _settings.PendingUpdateFxVersion : _settings.PendingUpdateVersion; set { if (OwnIsFx) _settings.PendingUpdateFxVersion = value; else _settings.PendingUpdateVersion = value; } }
    private string? RecSha256 { get => OwnIsFx ? _settings.PendingUpdateFxSha256 : _settings.PendingUpdateSha256; set { if (OwnIsFx) _settings.PendingUpdateFxSha256 = value; else _settings.PendingUpdateSha256 = value; } }
    private string? RecFrom { get => OwnIsFx ? _settings.PendingUpdateFxFrom : _settings.PendingUpdateFrom; set { if (OwnIsFx) _settings.PendingUpdateFxFrom = value; else _settings.PendingUpdateFrom = value; } }
    private string? RecSignedSha256 { get => OwnIsFx ? _settings.PendingUpdateFxSignedSha256 : _settings.PendingUpdateSignedSha256; set { if (OwnIsFx) _settings.PendingUpdateFxSignedSha256 = value; else _settings.PendingUpdateSignedSha256 = value; } }

    /// <summary>
    /// The cheap half of the record check (no hashing of the file): the record of <paramref name="folder"/>'s packaging is of
    /// <paramref name="version"/> and its signed hash (PendingUpdate[Fx]SignedSha256, written only by a download that verified against a
    /// signed manifest) is present and equals its recorded hash. Null if so, otherwise why not. A legacy <c>PendingUpdateSigned: true</c>
    /// counts as absent (it is not even read), so a download made before it was replaced is fetched again once.
    /// </summary>
    private string? RecordProblem(ReleaseVersion version, Packaging folder)
    {
        bool fx = folder == Packaging.Framework;
        string? recVersion = fx ? _settings.PendingUpdateFxVersion : _settings.PendingUpdateVersion;
        string? recSha = fx ? _settings.PendingUpdateFxSha256 : _settings.PendingUpdateSha256;
        string? signedSha = fx ? _settings.PendingUpdateFxSignedSha256 : _settings.PendingUpdateSignedSha256;
        if (!string.Equals(recVersion, version.ToString(), StringComparison.Ordinal) || string.IsNullOrEmpty(recSha))
            return "there is no record of a verified download of this version";
        if (string.IsNullOrEmpty(signedSha) || !string.Equals(signedSha, recSha, StringComparison.OrdinalIgnoreCase))
            return "it was not verified against a signed manifest, and this build installs signed updates only";
        return null;
    }

    internal static string FileFor(ReleaseVersion v)
    {
        // Only X.Y.Z and X.Y.Z-dev.N become a file name, and the result must be a direct child of the updates folder.
        string text = v.ToString();
        if (ReleaseVersion.ParseCanonical(text) == null) throw new ArgumentException($"version '{text}' is not X.Y.Z or X.Y.Z-dev.N", nameof(v));
        string dir = UpdatesDir;
        string path = Path.Combine(dir, $"Clink-{text}.exe");
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (parent == null || !string.Equals(parent.TrimEnd('\\', '/'), Path.GetFullPath(dir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"version '{text}' does not name a file inside the updates folder", nameof(v));
        return path;
    }

    /// <summary>
    /// Written by <see cref="LaunchApply"/> before the hand-over (so an apply process that never finishes counts as failed) and by
    /// <see cref="RunApplyUpdate"/> when the swap failed; deleted by a successful swap. Otherwise it stays until a newer download
    /// replaces the file or a manual check retries it.
    /// </summary>
    private static string FailedMarker(string exePath) => exePath + ".failed";

    /// <summary>Written by <see cref="RunApplyUpdate"/> after a successful swap, so the file is never installed a second time (even if the new exe reports an unexpected version).</summary>
    private static string InstalledMarker(string exePath) => exePath + ".installed";

    /// <summary>Counts the start-up checks of this file that only timed out (first line: the number). At <see cref="MaxStartupTimeouts"/> the file is marked failed instead, so start-up stops handing over to it; any fresh download or cleanup of the file removes it.</summary>
    private static string TimeoutMarker(string exePath) => exePath + ".timeout";

    /// <summary>How many start-up checks of one file may time out before the file counts as failed (the start-up then stops trying).</summary>
    internal const int MaxStartupTimeouts = 3;

    private static int TimeoutCount(string exePath)
    {
        try { return int.TryParse(File.ReadLines(TimeoutMarker(exePath)).FirstOrDefault()?.Trim(), out int n) && n > 0 ? n : 0; }
        catch { return 0; }
    }

    /// <summary>Removes the failed, installed and timeout markers of a file.</summary>
    private static void DeleteMarkers(string exePath)
    {
        TryDelete(FailedMarker(exePath));
        TryDelete(InstalledMarker(exePath));
        TryDelete(TimeoutMarker(exePath));
    }

    /// <summary>First line of the failed marker (why the install failed), or null if there is none.</summary>
    private static string? FailedReason(string exePath)
    {
        string marker = FailedMarker(exePath);
        try
        {
            if (!File.Exists(marker)) return null;
            string? line = File.ReadLines(marker).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(line) ? "unknown error" : line;
        }
        catch (Exception ex) { return $"cannot read {marker}: {ex.Message}"; }
    }

    /// <summary>Version in the name <c>Clink-{version}.exe</c> (the inverse of <see cref="FileFor"/>), or null.</summary>
    internal static ReleaseVersion? VersionFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("Clink-", StringComparison.OrdinalIgnoreCase) ? ReleaseVersion.ParseCanonical(name[6..]) : null;   // only the names FileFor makes
    }

    /// <summary>Version in an exe's version resource (ProductVersion = InformationalVersion, else FileVersion); null if unreadable.</summary>
    private static ReleaseVersion? EmbeddedVersion(string path)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return ReleaseVersion.Parse(fvi.ProductVersion) ?? ReleaseVersion.Parse(fvi.FileVersion);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ check

    /// <summary>What a check says, and the Updates card shows, when this build has no signed update feed (feed.json is empty): nothing is ever contacted.</summary>
    public const string NoUpdatesNote = "Updates aren't available in this build";

    /// <summary>
    /// Background checks that have failed without a single success for longer than this earn one tray notice per session
    /// (<c>checkfailed:&lt;channel&gt;</c>, see <see cref="TrackFailures"/>).
    /// </summary>
    internal static readonly TimeSpan FailingNoticeAfter = TimeSpan.FromDays(3);

    /// <summary>A stamp further ahead than this (a wrong clock, an edited settings.json) counts as absent.</summary>
    internal static readonly TimeSpan MaxStampFuture = TimeSpan.FromDays(1);

    /// <summary>A stamp as stored: <c>"&lt;version&gt;@&lt;UTC ISO-8601, round-trip&gt;"</c>, e.g. "0.4.2@2026-10-28T09:00:00.0000000Z".</summary>
    internal static string FormatStamp(ReleaseVersion version, DateTime utc) =>
        version + "@" + (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// The time of a stored stamp that was recorded for <paramref name="version"/>, or null: absent, unreadable (no "@", not the round-trip
    /// UTC form), recorded for another version, or more than <see cref="MaxStampFuture"/> after <paramref name="nowUtc"/>.
    /// </summary>
    internal static DateTime? ParseStamp(string? text, ReleaseVersion version, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 100) return null;
        int at = text.IndexOf('@');
        if (at <= 0 || !string.Equals(text[..at], version.ToString(), StringComparison.Ordinal)) return null;
        if (!DateTime.TryParseExact(text[(at + 1)..], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) || t.Kind != DateTimeKind.Utc) return null;
        return t > nowUtc + MaxStampFuture ? null : t;
    }

    /// <summary>The host of a feed base URL, for the plain messages ("Couldn't reach the update server (dl.example)"). Never anything a server supplied.</summary>
    private static string HostOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : "unknown host";

    /// <summary>
    /// Asks the signed feed for the newest release (stable channel) or the newest release or dev build (<see cref="Settings.UseDevBuilds"/>).
    /// Returns info only if <see cref="ShouldInstall(ReleaseVersion, ReleaseVersion, bool, bool)"/> accepts it; <paramref name="manual"/> allows
    /// going from a dev build back to an older release. Never throws (a cancelled <paramref name="ct"/> ends it with LastError "cancelled").
    /// <para>Updates not available (<see cref="UpdatesEnabled"/> false: the empty feed.json, a --dev instance): nothing is asked at all; LastError
    /// says so. A broken feed.json (<see cref="FeedState.Misconfigured"/>) likewise, with <see cref="MisconfiguredNote"/>.</para>
    /// <para>Otherwise stable.json (and, on the dev channel, dev.json too) is fetched from the channel's base with its signature, and the
    /// newest signed manifest is trusted as it is: there is no timestamp or freshness check, a quiet period without a release is no
    /// failure. Newer = offered; the same version or older = up to date. A fetch failure (unreachable, HTTP error, timeout, bad or
    /// missing signature, invalid manifest, a manifest below the channel's floor) fails the check with a plain message that names only the
    /// host that failed; there is no fallback to any other source. On the dev channel a verified newer build from the other manifest is
    /// still offered, but "up to date" is never said while either manifest failed (<see cref="CheckFeedAsync"/>). The floor (the highest
    /// version verified before, or the one built into the exe, whichever is higher) refuses a replayed lower version.</para>
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct, bool manual = false)
    {
        bool dev = _settings.UseDevBuilds;   // one channel for the whole check, even if the box is ticked meanwhile
        var trust = Trust;
        _failNotice = null;
        if (trust.Misconfigured) { LastError = MisconfiguredNote; CheckFailed = true; Log.Warn("updater: not checking: " + MisconfiguredNote); return null; }
        if (!UpdatesEnabled)
        {
            LastError = AppController.IsDev ? DevModeNote.ToLowerInvariant() : NoUpdatesNote;
            CheckFailed = true;
            return null;
        }
        string channelName = UpdateFeed.ChannelName(dev);
        string host = HostOf(trust.BaseFor(channelName));   // the failed host for the notice (TrackFailures); the outcome names the one that really failed
        try
        {
            var outcome = await CheckFeedAsync(trust, dev, manual, ct).ConfigureAwait(false);
            LastError = outcome.Error;
            if (outcome.FailedHost != null) host = outcome.FailedHost;
            string published = outcome.Used is { } used ? $", published {used.Published.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC" : "";
            if (outcome.Info is { } info) Log.Info($"updater: v{info.Version} available ({info.ExeSize / 1024} KB{published}) from the signed feed");
            else if (outcome.Error == null) Log.Info($"updater: up to date ({outcome.Why}{published}, running v{VersionDisplay})");
            return outcome.Info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LastError = "cancelled";
            return null;
        }
        catch (Exception ex)
        {
            LastError = $"Couldn't check for updates ({host})";
            Log.Warn("updater: check failed: " + ex.Message);
            return null;
        }
        finally
        {
            CheckFailed = LastError != null;   // every way out of the try sets LastError: null on success, the reason otherwise
            if (!ct.IsCancellationRequested) TrackFailures(channelName, host, manual);
            _settings.LastUpdateCheck = Now.UtcDateTime;
            _settings.Save();
        }
    }

    /// <summary>How many failed background passes in a row this session needs before the "could not check for updates" notice (with the streak over <see cref="FailingNoticeAfter"/>).</summary>
    internal const int FailingNoticeMinPasses = 2;
    /// <summary>Failed background passes of this session since the last successful check (<see cref="TrackFailures"/>).</summary>
    private int _failedBackgroundPasses;

    /// <summary>
    /// Keeps <see cref="Settings.FeedFloorFailingSince"/>: set at the first failed check (manual or background), cleared by any successful
    /// one. A failed background pass leaves <see cref="_failNotice"/> (a notification keyed <c>checkfailed:&lt;channel&gt;</c>, so once per
    /// session) only when the streak is older than <see cref="FailingNoticeAfter"/> AND this session has seen at least
    /// <see cref="FailingNoticeMinPasses"/> failed background passes since its last success: a laptop that wakes up offline after a week does
    /// not raise the alarm at its first pass. A manual check never does, its result is in the footer. The text names only the host that
    /// failed (<paramref name="host"/>) and the duration.
    /// </summary>
    private void TrackFailures(string channelName, string host, bool manual)
    {
        var now = Now.UtcDateTime;
        if (!CheckFailed) { _settings.FeedFloorFailingSince = null; _failedBackgroundPasses = 0; return; }
        if (!manual) _failedBackgroundPasses++;
        if (ParseStamp(_settings.FeedFloorFailingSince, Current, now) is not { } since)
        {
            since = now;   // none, another version's, unreadable or from the future: the streak starts now
            _settings.FeedFloorFailingSince = FormatStamp(Current, now);
        }
        if (manual || _failedBackgroundPasses < FailingNoticeMinPasses || now - since <= FailingNoticeAfter) return;
        int days = (int)(now - since).TotalDays;
        _failNotice = new UpdateNotice($"checkfailed:{channelName}", "Clink could not check for updates",
            $"Clink has not been able to check for updates from {host} for {days} days. Details under Alerts → Updates.");
    }

    /// <summary>
    /// What <see cref="CheckFeedAsync"/> found: the offer (or null), the plain error of a failed check (or null), why for the log,
    /// the host of the manifest that failed (or null), and the manifest the result rests on (for the log's <c>published</c>).
    /// </summary>
    private readonly record struct FeedOutcome(UpdateInfo? Info, string? Error, string Why, string? FailedHost = null, FeedManifest? Used = null);

    /// <summary>
    /// The feed part of <see cref="CheckAsync"/>: fetches and verifies stable.json (stable channel) or stable.json and dev.json (dev
    /// channel: a dev user must also get a newer release) and decides. Only manifests that verified (and passed their floor) raise their
    /// floor and count. On the dev channel a manifest that failed (unreachable, refused, or below its floor: all alike) does not hide what
    /// the other one offers: a verified install-worthy offer is taken (with a warning naming the failed host), but "up to date" is never
    /// said from half the picture: with nothing offered and any manifest failed, the check fails with that manifest's host-only message.
    /// Throws only for a cancelled <paramref name="ct"/>.
    /// </summary>
    private async Task<FeedOutcome> CheckFeedAsync(FeedTrust trust, bool dev, bool manual, CancellationToken ct)
    {
        var fetched = new List<FeedManifest>();
        (string Channel, string Host, string Error, string Why)? failure = null;   // the first manifest that failed
        foreach (string channel in dev ? new[] { "stable", "dev" } : new[] { "stable" })
        {
            // stable.json under feedUrl, dev.json under the dev base (devFeedUrl, or feedUrl when that is empty); binaries must sit under the same base.
            string feedBase = trust.BaseFor(channel)!;
            string host = HostOf(feedBase);
            string manifestUrl = feedBase + channel + ".json";
            try
            {
                var m = await UpdateFeed.FetchVerifiedAsync(GetFeedAsync, manifestUrl, manifestUrl + ".sig", channel, trust.KeysFor(channel), ct, FeedTimeoutOverride, feedBase).ConfigureAwait(false);
                if (UpdateFeed.FloorReason(m, Floor(channel)) is { } below)
                {
                    // A replayed older manifest (or a rolled-back feed): never used, and not an "up to date" either.
                    Log.Warn($"updater: {channel}.json from the signed feed was refused: {below}");
                    failure ??= (channel, host, $"The update server ({host}) offered an older version than before, so it was not used", below);
                    continue;
                }
                fetched.Add(m);
            }
            catch (FeedUnreachableException ex)
            {
                Log.Warn($"updater: {channel}.json could not be reached: {ex.Message}");
                failure ??= (channel, host, $"Couldn't reach the update server ({host})" + (ex.UserDetail != null ? ": " + ex.UserDetail : ""), ex.Message);
            }
            catch (FeedRejectedException ex)
            {
                // Fetched but not trustworthy: worth a warning (a broken publish, or someone serving a forged feed), never used.
                Log.Warn($"updater: {channel}.json from the signed feed was refused: {ex.Message}");
                failure ??= (channel, host, $"Couldn't verify the update server's answer ({host})", ex.Message);
            }
        }
        foreach (var m in fetched) RaiseFloor(m.Channel, m.Version);   // only what verified (and passed its floor) raises a floor
        var best = fetched.MaxBy(m => m.Version);
        if (best != null && ShouldInstall(best.Version, Current, dev, allowLeaveDev: manual))
        {
            if (failure is { } f)
                Log.Warn($"updater: offering v{best.Version} from {best.Channel}.json although {f.Channel}.json from {f.Host} failed ({f.Why})");
            return new(UpdateFeed.ToUpdateInfo(best, OwnPackaging), null, "", null, best);
        }
        if (failure is { } failed) return new(null, failed.Error, failed.Why, failed.Host);   // never "up to date" from half the picture
        if (best!.Version == Current) return new(null, null, $"the signed feed says v{best.Version}", null, best);
        // Older than the running exe (a dev build ahead of the releases, or a hand-installed newer build): nothing newer exists.
        return new(null, null, $"the signed feed's newest is v{best.Version}, older than the running v{Current}", null, best);
    }

    /// <summary>
    /// The floor of <paramref name="channel"/>: the higher of the highest version verified before (settings FeedFloorStable / FeedFloorDev)
    /// and the version this exe was built with as the newest published one (<see cref="FloorSeed"/>), or null when there is neither.
    /// </summary>
    private ReleaseVersion? Floor(string channel)
    {
        var saved = SavedFloor(channel);
        var seed = FloorSeed(channel);
        return saved is { } a && seed is { } b ? (a > b ? a : b) : saved ?? seed;
    }

    /// <summary>The floor settings.json holds for <paramref name="channel"/> (FeedFloorStable / FeedFloorDev), or null.</summary>
    private ReleaseVersion? SavedFloor(string channel) => ReleaseVersion.Parse(channel == "dev" ? _settings.FeedFloorDev : _settings.FeedFloorStable);

    /// <summary>Raises the saved floor of <paramref name="channel"/> to <paramref name="version"/> (saved with the check's settings save). The seed is not written: it is read from the exe every time.</summary>
    private void RaiseFloor(string channel, ReleaseVersion version)
    {
        if (SavedFloor(channel) is { } f && f >= version) return;
        if (channel == "dev") _settings.FeedFloorDev = version.ToString();
        else _settings.FeedFloorStable = version.ToString();
    }

    // ---- embedded floor seeds: AssemblyMetadata ClinkMinStable / ClinkMinDev (Clink.csproj; CI passes -p:ClinkMinStable=<X.Y.Z>
    // -p:ClinkMinDev=<X.Y.Z-dev.N>, the newest published versions when the exe was built). A fresh install (no saved floor) then still
    // refuses a replayed manifest older than what was out when it was built. A local build has none.

    /// <summary>The AssemblyMetadata keys of the floor seeds.</summary>
    internal const string MinStableKey = "ClinkMinStable", MinDevKey = "ClinkMinDev";

    /// <summary>Stands in for the exe's embedded floor seeds in the unit tests (the runner sets "none" for every test, so a CI build's seeds never change a test); null otherwise.</summary>
    internal static (string? Stable, string? Dev)? FloorSeedOverride { get; set; }

    private static readonly Lazy<(string? Stable, string? Dev)> s_embeddedSeeds = new(() =>
    {
        string? stable = null, dev = null;
        try
        {
            foreach (var a in typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (a.Key == MinStableKey) stable = a.Value;
                else if (a.Key == MinDevKey) dev = a.Value;
            }
        }
        catch (Exception) { }
        return (stable, dev);
    });

    private static string? SeedText(string channel)
    {
        var seeds = FloorSeedOverride ?? s_embeddedSeeds.Value;
        return channel == "dev" ? seeds.Dev : seeds.Stable;
    }

    /// <summary>The embedded floor seed of <paramref name="channel"/>, or null (none, or not a canonical version of that channel: ignored, see <see cref="SeedProblem"/>).</summary>
    internal static ReleaseVersion? FloorSeed(string channel) => ParseSeed(SeedText(channel), channel, out _);

    /// <summary>Why the embedded seed of <paramref name="channel"/> is ignored, or null (none, or a good one): for the startup log.</summary>
    internal static string? SeedProblem(string channel)
    {
        ParseSeed(SeedText(channel), channel, out string? problem);
        return problem;
    }

    /// <summary>
    /// A floor seed: empty = none; otherwise exactly X.Y.Z for stable and X.Y.Z-dev.N for dev (<see cref="ReleaseVersion.ParseCanonical"/>,
    /// no "v"). Anything else is ignored with <paramref name="problem"/> set (the client then has the saved floor only).
    /// </summary>
    internal static ReleaseVersion? ParseSeed(string? text, string channel, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var v = ReleaseVersion.ParseCanonical(text);
        bool fits = v is { } x && (channel == "dev" ? x.IsDevBuild : !x.IsPrerelease);
        if (!fits) { problem = $"the built-in {channel} floor '{(text.Length > 40 ? text[..40] + "…" : text)}' is not {(channel == "dev" ? "X.Y.Z-dev.N" : "X.Y.Z")}; ignored"; return null; }
        return v;
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Clink", CurrentVersion.ToString(3)));
    }

    // ------------------------------------------------------------------ download

    /// <summary>
    /// Downloads and verifies the release exe; returns the path of <c>Clink-{version}.exe</c> in the updates folder and records
    /// the verified SHA-256 in settings for <see cref="TryGetPendingUpdate"/>. Throws on any failure.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (AppController.IsDev) throw new InvalidOperationException(DevModeNote);
        if (!UpdatesEnabled) throw new InvalidOperationException(NoUpdatesNote);   // no signed feed, no download: not even a request is made
        Directory.CreateDirectory(UpdatesDir);
        string final = FileFor(info.Version);
        string partial = final + ".partial";
        IsDownloading = true;
        try
        {
            // The hash and size come from the signed manifest (UpdateInfo): nothing unsigned is ever downloaded, and no hash file is requested.
            if (string.IsNullOrEmpty(info.Sha256)) throw Shown(new InvalidOperationException("the offer carries no signed SHA-256"), "the offer carries no signed SHA-256");
            string expected = info.Sha256;
            long done = 0;
            using (var req = new HttpRequestMessage(HttpMethod.Get, info.ExeUrl))
            {
                AddHeaders(req);
                // A feed binary comes from the feed's host, which never redirects (s_feedHttp).
                // The answer (connect + headers) within DownloadAnswerTimeout: a host that accepts the connection and never answers must not
                // hold the updater's gate, and with it every later check, for the rest of the session. The body has its per-read deadline below.
                HttpResponseMessage answer;
                using (var answerLimit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    answerLimit.CancelAfter(DownloadAnswerTimeout);
                    try { answer = await SendAsync(req, answerLimit.Token, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        string text = $"the download server did not answer within {DownloadAnswerTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s";
                        throw Shown(new IOException(text), text);
                    }
                    answerLimit.CancelAfter(Timeout.InfiniteTimeSpan);   // answered: the timer must not fire into the body's reads
                }
                using var resp = answer;
                // An explicit status check: EnsureSuccessStatusCode's message carries the server's reason phrase, and this text reaches the user.
                if (!resp.IsSuccessStatusCode) throw Shown(new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode), $"HTTP {(int)resp.StatusCode}");
                // With a signed size, a body that says or turns out to be longer is cut off instead of being transferred in full.
                if (resp.Content.Headers.ContentLength is long declared && declared != info.ExeSize)
                    throw Shown(new InvalidDataException($"size mismatch: the server announces {declared} bytes, the signed manifest says {info.ExeSize}"), SizeMismatch);
                long total = resp.Content.Headers.ContentLength ?? info.ExeSize;
                var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                // Disposed with ConfigureAwait(false) too: no continuation of this method needs the caller's (UI) context.
                await using (src.ConfigureAwait(false))
                {
                    var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await using (dst.ConfigureAwait(false))
                    {
                        var buf = new byte[1 << 16];
                        // HttpClient.Timeout only covers the headers once the body is streamed; a transfer that goes quiet must not hold
                        // the updater (and its gate) for the rest of the session, so every read gets its own inactivity deadline.
                        // Inactivity alone is not enough (one byte every 59 s never goes quiet), so the body also has a total deadline
                        // (DownloadDeadline) and a minimum average rate over a sliding window; both end in an IOException like a stall,
                        // which the callers report as "downloading vN failed" and count against the attempt cap.
                        TimeSpan deadlineLimit = DownloadDeadline(Math.Max(info.ExeSize, resp.Content.Headers.ContentLength ?? 0));   // clamped to MaxExeBytes inside
                        using var deadline = new CancellationTokenSource(deadlineLimit);
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                        var clock = Stopwatch.StartNew();
                        var window = DownloadRateWindowOverride ?? RateWindow;
                        long minRate = DownloadMinRateOverride ?? MinDownloadBytesPerSecond;
                        // The rate is averaged from the anchor: the newest sample at or before the window's start. Samples are taken at most
                        // once per RateSampleSpacing, so the queue holds about window / spacing + 1 entries (61 for the real 60 s window)
                        // whatever the read size: a server that sends one byte per read must not grow it without bound.
                        var samples = new Queue<(TimeSpan At, long Done)>();
                        (TimeSpan At, long Done) anchor = (TimeSpan.Zero, 0);
                        TimeSpan lastSample = TimeSpan.Zero;
                        RateSamplesPeak = 0;
                        try
                        {
                            int n;
                            idle.CancelAfter(DownloadReadTimeout);
                            while ((n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false)) > 0)
                            {
                                idle.CancelAfter(DownloadReadTimeout);
                                await dst.WriteAsync(buf.AsMemory(0, n), idle.Token).ConfigureAwait(false);
                                done += n;
                                var at = clock.Elapsed;
                                if (at - lastSample >= RateSampleSpacing) { samples.Enqueue((at, done)); lastSample = at; }
                                while (samples.Count > 0 && samples.Peek().At <= at - window) anchor = samples.Dequeue();
                                if (samples.Count > RateSamplesPeak) RateSamplesPeak = samples.Count;
                                if (at - anchor.At >= window)
                                {
                                    double rate = (done - anchor.Done) / (at - anchor.At).TotalSeconds;
                                    if (rate < minRate)
                                        throw Shown(new IOException($"download too slow ({rate / 1024:0.#} KB/s over the last {(at - anchor.At).TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s, at least {minRate / 1024.0:0.#} KB/s needed; {done / 1024} KB so far)"),
                                            $"download too slow (under {(minRate / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB/s)");
                                }
                                if (done > info.ExeSize) throw Shown(new InvalidDataException($"size mismatch: more than the {info.ExeSize} bytes the signed manifest says"), SizeMismatch);
                                if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
                        {
                            string over = deadlineLimit.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
                            throw Shown(new IOException($"download took too long (over {over} s in total, {done / 1024} KB received)"), $"download took too long (over {over} s)");
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            string quiet = DownloadReadTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);
                            throw Shown(new IOException($"download stalled (no data for {quiet} s after {done / 1024} KB)"), $"download stalled (no data for {quiet} s)");
                        }
                    }
                }
            }

            long length = new FileInfo(partial).Length;
            if (info.ExeSize > 0 && length != info.ExeSize) throw Shown(new InvalidDataException($"size mismatch: got {length} bytes, the signed manifest says {info.ExeSize}"), SizeMismatch);
            if (!LooksLikeExe(partial)) throw Shown(new InvalidDataException("downloaded file is not a Windows executable"), "the downloaded file is not a Windows executable");
            string actual = Sha256Hex(partial);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw Shown(new InvalidDataException("SHA-256 mismatch"), "SHA-256 mismatch");
            string? wrongVersion = CheckEmbeddedVersion(partial, info.Version);
            if (wrongVersion != null) throw Shown(new InvalidDataException($"release {info.Tag} is unusable: {wrongVersion}"), $"the downloaded exe does not report v{info.Version}");
            progress?.Report(1.0);

            File.Move(partial, final, overwrite: true);
            RecVersion = info.Version.ToString();
            RecSha256 = actual;
            // See RecordProblem(): only a download whose recorded hash is this signed one is ever installed or reused. A new key that
            // v0.4.1 and older do not know: a download of theirs rewrites PendingUpdateSha256 but leaves this as it was, and they no longer match.
            RecSignedSha256 = actual;
            RecFrom = Current.ToString();   // see LeaveDevAllowedAtStart()
            _settings.Save();
            DeleteMarkers(final);
            foreach (string other in Directory.EnumerateFiles(UpdatesDir, "Clink-*.exe"))
            {
                if (!other.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(other);
                DeleteMarkers(other);
            }
            foreach (string stale in Directory.EnumerateFiles(UpdatesDir, "*.partial")) TryDelete(stale);
            return final;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        finally { IsDownloading = false; }
    }

    // ---- what the user is told about a failed download: fixed texts only (like UpdateFeed.FetchCappedAsync's UserDetail), never an
    // exception's own message, which may carry what a server sent (a header line, a reason phrase). The log has the full message.

    private const string UserTextKey = "Clink.Updater.UserText";
    private const string SizeMismatch = "size mismatch";

    /// <summary><paramref name="ex"/>, carrying <paramref name="userText"/> (built here from constants and numbers, never from a server) for <see cref="DownloadErrorText"/>.</summary>
    private static T Shown<T>(T ex, string userText) where T : Exception
    {
        ex.Data[UserTextKey] = userText;
        return ex;
    }

    /// <summary>
    /// The short text the user is shown for a failed exe download from <paramref name="url"/>: the fixed text <see cref="DownloadAsync"/>
    /// attached ("HTTP 503", "download stalled (no data for 60 s)", "SHA-256 mismatch", ...), else one chosen by the kind of failure
    /// ("network error (&lt;host&gt;)"). Never <see cref="Exception.Message"/>.
    /// </summary>
    internal static string DownloadErrorText(Exception ex, string url)
    {
        if (ex.Data[UserTextKey] is string text) return text;
        if (ex is OperationCanceledException) return $"no answer within {FeedClientTimeout.TotalSeconds:0} s";
        if (ex is HttpRequestException { StatusCode: { } code }) return $"HTTP {(int)code}";
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is HttpRequestException or HttpIOException or System.Net.Sockets.SocketException) return $"network error ({HostOf(url)})";
        if (ex is IOException or UnauthorizedAccessException) return "the download could not be saved in the updates folder";
        return "unexpected error (details in the log)";
    }

    private static string Sha256Hex(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static bool LooksLikeExe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[2];
            return fs.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
        }
        catch { return false; }
    }

    /// <summary>
    /// The exe must report the version its release promises; otherwise installing it would loop forever (the restarted exe
    /// would still consider the same file newer than itself). The pre-release suffix must match too ("0.3.1-dev.57" is not "0.3.1").
    /// Returns null if fine, otherwise why not.
    /// </summary>
    private static string? CheckEmbeddedVersion(string file, ReleaseVersion expected)
    {
        var embedded = EmbeddedVersion(file);
        if (embedded == null) { Log.Warn($"updater: {file} has no readable version resource"); return null; }
        return EmbeddedVersionProblem(embedded.Value, expected);
    }

    /// <summary>The comparison of <see cref="CheckEmbeddedVersion"/>: null if the exe reports exactly <paramref name="expected"/> (suffix included), otherwise why not.</summary>
    internal static string? EmbeddedVersionProblem(ReleaseVersion embedded, ReleaseVersion expected) =>
        embedded != expected ? $"the exe reports v{embedded} instead of v{expected}" : null;

    /// <summary>
    /// Re-checks a downloaded <c>Clink-&lt;ver&gt;.exe</c> against what was verified when it was downloaded (hash recorded in
    /// settings) and the version it reports, so a file corrupted or swapped in the updates folder since then is never
    /// installed. Returns null if fine, otherwise why not.
    /// </summary>
    private string? VerifyDownload(string file, ReleaseVersion version) => VerifyDownload(file, version, OwnPackaging);

    /// <summary>
    /// <see cref="VerifyDownload(string, ReleaseVersion)"/> against the download record of <paramref name="folder"/>'s packaging: the legacy
    /// PendingUpdate* keys for the root updates folder, the PendingUpdateFx* keys for <c>updates\fx</c>.
    /// </summary>
    private string? VerifyDownload(string file, ReleaseVersion version, Packaging folder)
    {
        bool fx = folder == Packaging.Framework;
        string? recSha = fx ? _settings.PendingUpdateFxSha256 : _settings.PendingUpdateSha256;
        if (!LooksLikeExe(file)) return "it is not a Windows executable";
        if (RecordProblem(version, folder) is { } record) return record;   // the version, and signed hash == recorded hash
        string actual;
        try { actual = Sha256Hex(file); }
        catch (Exception ex) { return "it cannot be read: " + ex.Message; }
        // ... == the file's own hash
        if (!string.Equals(actual, recSha, StringComparison.OrdinalIgnoreCase)) return "its SHA-256 no longer matches the verified download";
        return CheckEmbeddedVersion(file, version);
    }

    /// <summary>
    /// True when the download recorded in settings is of <paramref name="info"/>'s version, its recorded SHA-256 is the one
    /// <paramref name="info"/> expects (the signed manifest's), and the signed hash written by that download is the same one. Only then
    /// may an existing download be reused for a signed offer (the file's own hash is checked by <see cref="VerifyDownload(string, ReleaseVersion)"/>).
    /// </summary>
    private bool RecordedDownloadIs(UpdateInfo info) =>
        RecordProblem(info.Version, OwnPackaging) == null
        && string.Equals(RecSha256, info.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RecSignedSha256, info.Sha256, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Warn($"updater: could not delete {path}: {ex.Message}"); }
    }

    /// <summary>
    /// The page to open for <paramref name="info"/>: the signed manifest's <c>notesUrl</c> when it is https and strictly under the base of
    /// the feed the manifest came from (<see cref="UpdateFeed.NotesPage"/>, the normalised <see cref="Uri.AbsoluteUri"/>), else the
    /// constant <see cref="UpdateFeed.FallbackNotesPage"/>. Nothing else a manifest says is ever handed to the shell. The notes live at
    /// <c>&lt;stable base&gt;releases/&lt;tag&gt;/notes.txt</c> and <c>&lt;dev base&gt;dev/&lt;tag&gt;/notes.txt</c>, next to the binaries.
    /// </summary>
    public string ReleasePageFor(UpdateInfo? info)
    {
        if (info == null) return UpdateFeed.FallbackNotesPage;
        var trust = Trust;
        string? feedBase = trust.On ? trust.BaseFor(UpdateFeed.ChannelName(info.Version.IsDevBuild)) : null;   // dev.json only ever carries dev builds, stable.json releases
        return UpdateFeed.NotesPage(info.NotesUrl, feedBase) ?? UpdateFeed.FallbackNotesPage;
    }

    /// <summary>Replaces the browser in the unit tests (told the one address that would be opened); null otherwise.</summary>
    internal static Action<string>? BrowserOverride { get; set; }

    /// <summary>Opens <see cref="ReleasePageFor"/> in the browser. Null when it was opened, otherwise a message for the footer.</summary>
    public string? OpenReleasePage(UpdateInfo? info)
    {
        string page = ReleasePageFor(info);
        try
        {
            if (BrowserOverride != null) BrowserOverride(page);
            else Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) { return "Could not open the browser: " + Describe(ex); }
    }

    private static string Describe(Exception? ex)
    {
        string s = (ex?.Message ?? "unknown error").ReplaceLineEndings(" ").Trim();
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    // ------------------------------------------------------------------ pending / apply

    private static string HowToInstallByHand => $"install it by hand from {UpdatesDir}";

    /// <summary>A manual check is what clears a <c>.failed</c> marker (see <see cref="CheckAndMaybeDownloadAsync"/>).</summary>
    private const string RetryHint = "To retry, click Check now under Alerts → Updates.";

    /// <summary>Why this process cannot install an update over <paramref name="targetExe"/> (it is elevated, or the folder is read-only), or null.</summary>
    public static string? InstallProblemFor(string targetExe)
    {
        // An elevated Clink must not run an exe from a folder every process of this user can write to.
        if (IsElevated()) return ElevatedNote;
        string dir = Path.GetDirectoryName(targetExe) ?? "";
        try
        {
            string probe = Path.Combine(dir, $"Clink.write-test.{Environment.ProcessId}");
            using var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return null;
        }
        catch (Exception) { return $"Clink.exe's folder ({dir}) is not writable"; }
    }

    /// <summary>Stands in for "is this process elevated" in the unit tests; null otherwise.</summary>
    internal static bool? IsElevatedOverride { get; set; }

    /// <summary>Exit code of <c>--apply-update</c> when it refuses to run elevated.</summary>
    internal const int ElevatedExitCode = 3;

    private const string ElevatedNote = "Clink is running as administrator and does not install updates from an elevated process";

    private static bool IsElevated()
    {
        if (IsElevatedOverride is bool forced) return forced;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Newest downloaded <c>Clink-&lt;ver&gt;.exe</c> that is newer than the running exe and still matches what was verified at
    /// download time. False (and nothing is run) when this build has no signed update feed, the file was already installed from,
    /// failed to install earlier, or cannot be installed by this process; <see cref="InstallProblem"/> then carries the message
    /// for the user where there is something they can do about it.
    /// </summary>
    public bool TryGetPendingUpdate(out string path, out ReleaseVersion version)
    {
        path = ""; version = Current; InstallProblem = null;
        try
        {
            // A build without a signed feed never downloaded anything; the updates folder is shared by every Clink exe on the machine.
            if (!UpdatesEnabled || !Directory.Exists(UpdatesDir)) return false;
            string? best = null; ReleaseVersion? bestNullable = null;
            foreach (string file in Directory.EnumerateFiles(UpdatesDir, "Clink-*.exe"))
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (VersionFromFile(file) is not { } v) continue;
                if (!ShouldInstall(v, allowLeaveDev: LeaveDevAllowedAtStart(v)) || (bestNullable is { } b && v <= b)) continue;
                best = file; bestNullable = v;
            }
            if (best == null || bestNullable is not { } bestVer) return false;

            if (File.Exists(InstalledMarker(best)))
            {
                Log.Warn($"updater: v{bestVer} was already installed from {best} but this exe is v{VersionDisplay}; not installing it again");
                return false;
            }
            // The cheap record checks (version, signed hash) before the failed marker: a download that is not ours to install at all (an
            // older build's unsigned one, which may have left a .failed marker) is thrown away quietly, not reported at every start.
            string? reason = RecordProblem(bestVer, OwnPackaging);
            if (reason != null)
            {
                Log.Warn($"updater: discarding {best}: {reason}");
                TryDelete(best);
                DeleteMarkers(best);
                return false;
            }
            reason = FailedReason(best);
            if (reason != null)
            {
                // Advice from the current state: retrying only helps where this process could install at all. The reason is
                // capped so the advice still fits in a tray balloon (250 characters); the log has it in full.
                string? blocked = InstallProblemFor(Environment.ProcessPath ?? "");
                string shown = reason.TrimEnd('.');
                if (shown.Length > 150) shown = shown[..150] + "…";
                InstallProblem = blocked != null
                    ? $"Update to v{bestVer} could not be installed: {shown}; {blocked}, so {HowToInstallByHand}."
                    : $"Update to v{bestVer} could not be installed: {shown}. {RetryHint}";
                Log.Warn($"updater: skipping v{bestVer}, installing it failed earlier: {reason}");
                return false;
            }
            reason = VerifyDownload(best, bestVer);
            if (reason != null)
            {
                Log.Warn($"updater: discarding {best}: {reason}");
                TryDelete(best);
                DeleteMarkers(best);
                return false;
            }
            reason = InstallProblemFor(Environment.ProcessPath ?? "");
            if (reason != null)
            {
                InstallProblem = $"Update to v{bestVer} is downloaded but {reason}; {HowToInstallByHand}.";
                Log.Warn($"updater: not installing v{bestVer}: {reason}");
                return false;
            }
            path = best; version = bestVer;
            return true;
        }
        catch (Exception ex) { Log.Warn("updater: pending scan failed: " + ex.Message); return false; }
    }

    /// <summary>Records why starting a downloaded update failed, in words the user can act on (shown once at startup).</summary>
    public void ReportInstallFailure(Exception ex, ReleaseVersion version, string pendingExe)
    {
        if (ex is ApplyRefusedException refused)
        {
            // PrepareApply said it all and, where the file is to blame, already wrote the marker (or removed the file): neither is redone here.
            InstallProblem = RefusalText(refused, version);
            Log.Warn("updater: " + InstallProblem);
            return;
        }
        bool appControl = ex.Message.Contains("Application Control", StringComparison.OrdinalIgnoreCase);
        // Kept short: this text comes back at every start inside a longer message, and tray balloons stop at 250 characters.
        string why = appControl
            ? "Smart App Control blocked it (Clink is not code-signed yet). Turn it off in Windows Security › App & browser control, or build Clink from source"
            : Describe(ex);
        InstallProblem = $"Update to v{version} could not start: {Shorten(why)}. {RetryHint}";
        Log.Warn("updater: " + InstallProblem);
        // Same marker the apply process writes: this file is not offered again at every start (a manual check retries it).
        try { File.WriteAllText(FailedMarker(pendingExe), $"{why}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} launch failed"); } catch { }
    }

    /// <summary>Longest reason put into a user message: with "Update to v…" in front and the retry hint behind it the whole stays within a tray balloon's 250 characters.</summary>
    internal const int ReasonCap = 145;

    /// <summary>What the user is told when <see cref="PrepareApply"/> refused (a status line or a balloon: at most 250 characters).</summary>
    internal static string RefusalText(ApplyRefusedException refused, ReleaseVersion version) => refused.Deferred
        ? $"Update to v{version} was not installed this time: {Shorten(refused.Reason)}. It will be tried again."
        : $"Update to v{version} not installed: {Shorten(refused.Reason)}. {RetryHint}";

    internal static string Shorten(string reason)
    {
        string s = reason.ReplaceLineEndings(" ").Trim().TrimEnd('.');
        return s.Length > ReasonCap ? s[..ReasonCap] + "…" : s;
    }

    // ------------------------------------------------------------------ preflight

    /// <summary>The early verb a downloaded exe is run with before it is applied: it does nothing and exits 0 (App.xaml.cs).</summary>
    internal const string PreflightVerb = "--preflight";
    /// <summary>How long a downloaded exe gets to start and exit 0 under <see cref="PreflightVerb"/> (a self-contained exe unpacks itself first).</summary>
    internal static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(15);
    /// <summary>The same at start-up, where nothing else is waiting for the answer (a first run of an exe the antivirus is still scanning can be slow).</summary>
    internal static readonly TimeSpan StartupPreflightTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How a preflight run ended: its exit code, or that it timed out (and was killed), or why it could not be started.</summary>
    internal readonly record struct PreflightResult(int? ExitCode, bool TimedOut, string? Error);

    /// <summary>Runs an exe with <see cref="PreflightVerb"/>, waits up to the timeout. Replaced by a fake in the unit tests (no test starts a real process); null otherwise.</summary>
    internal static Func<string, TimeSpan, PreflightResult>? PreflightOverride { get; set; }

    private static PreflightResult RunPreflightProcess(string exe, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            psi.ArgumentList.Add(PreflightVerb);
            psi.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";   // a missing runtime must fail quietly (exit code), not open a dialog
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start the process");
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new(null, true, null);
            }
            return new(p.ExitCode, false, null);
        }
        catch (Exception ex) { return new(null, false, Describe(ex)); }
    }

    /// <summary>
    /// Why the downloaded <paramref name="exe"/> cannot be applied, or null when it started and exited 0 under <see cref="PreflightVerb"/>.
    /// Catches a download that cannot run here at all (missing .NET runtime for the framework flavour, blocked by AV or App Control)
    /// before the installed exe is replaced by it.
    /// </summary>
    internal static string? PreflightProblem(string exe, TimeSpan timeout, Func<string, TimeSpan, PreflightResult>? launcher = null, Packaging flavour = Packaging.SelfContained) =>
        PreflightCheck(exe, timeout, launcher, flavour).Problem;

    /// <summary><see cref="PreflightProblem"/>, and whether the run only timed out (which says nothing against the file). The runtime hint is for the Framework flavour only.</summary>
    internal static (string? Problem, bool TimedOut) PreflightCheck(string exe, TimeSpan timeout, Func<string, TimeSpan, PreflightResult>? launcher = null, Packaging flavour = Packaging.SelfContained)
    {
        var r = (launcher ?? PreflightOverride ?? RunPreflightProcess)(exe, timeout);
        if (r.Error != null) return ($"it could not be started ({r.Error})", false);
        if (r.TimedOut) return ($"it did not finish its start-up check within {timeout.TotalSeconds:0} s", true);
        if (r.ExitCode != 0)
            return ($"its start-up check failed (exit code {r.ExitCode?.ToString() ?? "unknown"}{(flavour == Packaging.Framework ? "; if this is the framework build, is the .NET 10 Desktop Runtime installed?" : "")})", false);
        return (null, false);
    }

    /// <summary>
    /// A downloaded exe that <see cref="PrepareApply"/> would not hand over to. <see cref="Reason"/> is the short why (no "not applied" prefix).
    /// <see cref="Deferred"/>: the file is not to blame (its start-up check only took too long): nothing was marked failed, it is tried again.
    /// </summary>
    internal sealed class ApplyRefusedException : InvalidOperationException
    {
        public string Reason { get; }
        public bool Deferred { get; }
        public ApplyRefusedException(string message, string reason, bool deferred = false) : base(message) { Reason = reason; Deferred = deferred; }
    }

    /// <summary>A downloaded exe that was verified and passed its start-up check just now; the only way to <see cref="StartApply"/>. Made by <see cref="PrepareApply"/> alone.</summary>
    public sealed class ApplyToken
    {
        internal string Exe { get; }
        internal ReleaseVersion Version { get; }
        private ApplyToken(string exe, ReleaseVersion version) { Exe = exe; Version = version; }
        internal static ApplyToken Make(string exe, ReleaseVersion version) => new(exe, version);
    }

    /// <summary>Which download record and flavour a file of the updates folder belongs to: <c>updates\</c> = SelfContained (legacy keys), <c>updates\fx\</c> = Framework. Null for any other place.</summary>
    internal static Packaging? FlavourOfFolder(string file)
    {
        try
        {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ""));
            foreach (var p in new[] { Packaging.SelfContained, Packaging.Framework })
                if (string.Equals(dir, Path.TrimEndingDirectorySeparator(Path.GetFullPath(UpdatesDirFor(p))), StringComparison.OrdinalIgnoreCase)) return p;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Everything that must hold before a downloaded exe is run to replace the installed one, checked now (not when it was downloaded):
    /// this process is not elevated; the file is in one of the updates folders and re-hashed against that folder's own download record
    /// (a file that fails is thrown away like <see cref="TryGetPendingUpdate"/> does, and the pending update dropped); then the
    /// exe's start-up check (<see cref="PreflightVerb"/>) passes. The version is the one in the file's name and the record is looked up,
    /// never supplied by the caller. Blocking (a hash of tens of MB, a process of up to <paramref name="timeout"/>): call it off the UI thread.
    /// A failed start-up check marks the file failed, but a timeout does not (<see cref="ApplyRefusedException.Deferred"/>).
    /// </summary>
    public ApplyToken PrepareApply(string newExePath, TimeSpan? timeout = null) => PrepareApply(newExePath, timeout, countTimeouts: false);

    /// <summary>
    /// <see cref="PrepareApply(string, TimeSpan?)"/>. Takes the updater's gate for its whole run, as a try-lock (a running check or download
    /// may be replacing or deleting this very file): busy means a deferred refusal with nothing touched. <paramref name="countTimeouts"/>
    /// (the start-up path) counts a timed-out start-up check in the file's <c>.timeout</c> marker and marks the file failed at
    /// <see cref="MaxStartupTimeouts"/>, so a start never hangs on the same slow file for ever.
    /// </summary>
    internal ApplyToken PrepareApply(string newExePath, TimeSpan? timeout, bool countTimeouts)
    {
        if (!_gate.Wait(0))
        {
            const string busy = "a check or download is running; try again in a moment";
            Log.Warn($"updater: not applying {newExePath} now: {busy}");
            throw new ApplyRefusedException("the downloaded exe was not applied now: " + busy, busy, deferred: true);
        }
        try { return PrepareApplyLocked(newExePath, timeout, countTimeouts); }
        finally { _gate.Release(); }
    }

    private ApplyToken PrepareApplyLocked(string newExePath, TimeSpan? timeout, bool countTimeouts)
    {
        if (AppController.IsDev) throw new InvalidOperationException(DevModeNote);
        if (!UpdatesEnabled)
        {
            // No signed feed in this build (or a broken feed.json): nothing is installed, whatever the updates folder holds. Nothing is touched either.
            string why = Trust.Misconfigured ? "this build's update configuration (feed.json) is broken" : "this build has no updates";
            Log.Warn($"updater: not applying {newExePath}: {why}");
            throw new ApplyRefusedException("the downloaded exe was not applied: " + why, why);
        }
        if (IsElevated()) throw new ApplyRefusedException(ElevatedNote, ElevatedNote);
        if (FlavourOfFolder(newExePath) is not { } flavour || VersionFromFile(newExePath) is not { } version)
        {
            string why = "it is not a Clink-<version>.exe in the updates folder";
            Log.Warn($"updater: not applying {newExePath}: {why}");
            throw new ApplyRefusedException("the downloaded exe was not applied: " + why, why);
        }
        if (VerifyDownload(newExePath, version, flavour) is { } bad)
        {
            Log.Warn($"updater: discarding {newExePath}: {bad}");
            TryDelete(newExePath);
            DeleteMarkers(newExePath);
            DropStalePending();   // the file is gone: no "Update to v…" for it any more
            string why = $"{bad}; the file was removed";
            throw new ApplyRefusedException($"the downloaded exe was not applied: {why}", why);
        }
        // Does the downloaded exe even start here? If not it is marked failed (like a bad hash) and the current version keeps running.
        var (preflight, timedOut) = PreflightCheck(newExePath, timeout ?? PreflightTimeout, null, flavour);
        if (preflight != null)
        {
            if (timedOut)
            {
                // Slow, not broken (antivirus scanning a fresh exe, a busy disk): no failed marker, so the next start or the next click
                // tries again. At start-up the timeouts are counted, and the third one marks the file failed (a manual check retries it).
                int count = countTimeouts ? TimeoutCount(newExePath) + 1 : 0;
                if (countTimeouts && count < MaxStartupTimeouts)
                    try { File.WriteAllText(TimeoutMarker(newExePath), $"{count}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} start-up check timed out"); } catch { }
                if (!countTimeouts || count < MaxStartupTimeouts)
                {
                    Log.Warn($"updater: not applying {newExePath} now: {preflight}");
                    throw new ApplyRefusedException("the downloaded exe was not applied now: " + preflight, preflight, deferred: true);
                }
                Log.Warn($"updater: {preflight} at {count} starts in a row ({newExePath}); marking it failed");
                try { File.WriteAllText(FailedMarker(newExePath), $"{preflight}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} start-up check timed out {count} times"); } catch { }
                TryDelete(TimeoutMarker(newExePath));
                throw new ApplyRefusedException("the downloaded exe was not applied: " + preflight, preflight);
            }
            string why = "the downloaded exe was not applied: " + preflight;
            Log.Warn($"updater: {why} ({newExePath})");
            try { File.WriteAllText(FailedMarker(newExePath), $"{preflight}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} preflight failed"); } catch { }
            throw new ApplyRefusedException(why, preflight);
        }
        TryDelete(TimeoutMarker(newExePath));
        return ApplyToken.Make(newExePath, version);
    }

    /// <summary><see cref="PrepareApply"/> then <see cref="StartApply"/>: the start-up path, which has no window to keep responsive.</summary>
    public void LaunchApply(string newExePath, string targetExePath, IEnumerable<string> restartArgs, TimeSpan? timeout = null) =>
        StartApply(PrepareApply(newExePath, timeout ?? StartupPreflightTimeout, countTimeouts: true), targetExePath, restartArgs);

    /// <summary>
    /// Starts the downloaded exe of <paramref name="token"/> so it replaces <paramref name="targetExePath"/> once this process has exited
    /// and then restarts it with <paramref name="restartArgs"/> (the current command line, so a login start stays in the tray). The caller then exits.
    /// </summary>
    public static void StartApply(ApplyToken token, string targetExePath, IEnumerable<string> restartArgs)
    {
        if (AppController.IsDev) throw new InvalidOperationException(DevModeNote);
        if (IsElevated()) throw new ApplyRefusedException(ElevatedNote, ElevatedNote);
        string newExePath = token.Exe;
        var psi = new ProcessStartInfo(newExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(targetExePath) ?? "",
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(targetExePath);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        foreach (string a in restartArgs) psi.ArgumentList.Add(a);
        Log.Info($"updater: launching {newExePath} {string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? '"' + a + '"' : a))}");
        // Count it as failed until the apply process says otherwise (it deletes this marker on success and rewrites it on
        // failure): a downloaded exe that cannot run at all (missing .NET runtime, crash before --apply-update, killed by AV)
        // must not be handed over to again at every start.
        string marker = FailedMarker(newExePath);
        try { File.WriteAllText(marker, $"the update process did not finish\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} {targetExePath}"); } catch { }
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start the updater process");
            // It waits for this process to exit before doing anything, so leaving early always means it failed.
            if (p.WaitForExit(2000)) throw new InvalidOperationException($"the update process exited right away (exit code {p.ExitCode})");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(marker, $"{Describe(ex)}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} launch failed"); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Entry point of <c>Clink.exe --apply-update &lt;targetExePath&gt; &lt;pid&gt; [restart args...]</c>, run from the downloaded exe:
    /// waits for the old process, stages a copy of this exe next to the target as <c>Clink.exe.new</c>, swaps it in by renaming
    /// (the previous exe stays as <c>Clink.exe.bak</c> until the new one has started and cleaned up) and restarts the target with
    /// the original command line. If the swap fails the previous exe is left in place and a <c>.failed</c> marker says why.
    /// </summary>
    public static int RunApplyUpdate(string[] args)
    {
        string target = args.Length > 1 ? args[1] : "";
        int pid = args.Length > 2 && int.TryParse(args[2], out int p) ? p : 0;
        string[] restartArgs = args.Length > 3 ? args[3..] : Array.Empty<string>();
        string self = Environment.ProcessPath ?? "";

        // An elevated process never swaps the installed exe (the downloaded exe sits in a folder every process of this user can write to).
        // The target is not touched, nothing is restarted and this process writes nothing at all (no log, no marker: an elevated
        // process must not write into the user's folders). The parent may still write a .failed marker when it sees this child exit early.
        if (IsElevated())
        {
            try { Console.Error.WriteLine($"Clink --apply-update: {ElevatedNote}"); } catch { }
            return ElevatedExitCode;
        }

        // Wait for the old process first: it still holds the log file open (and the exe we are about to replace).
        string waitNote = "old process already gone";
        if (pid > 0)
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                waitNote = old.WaitForExit(60_000) ? "old process exited" : "old process did not exit within 60 s, trying anyway";
            }
            catch (ArgumentException) { }
            catch (Exception ex) { waitNote = "waiting for old process: " + ex.Message; }
        }

        Log.Init(Settings.Dir);
        Log.Info($"[update] apply: {self} -> {target} (pid {pid}: {waitNote})");
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(self)) { Log.Error("[update] missing target or own path"); return 2; }

        string staged = target + ".new", backup = target + ".bak";
        Exception? last = null;
        bool installed = false;
        try
        {
            // 1. Stage a copy next to the target: a failed or interrupted copy never touches the installed exe.
            bool copied = false;
            for (int i = 0; i < 5 && !copied; i++)
            {
                try { File.Copy(self, staged, overwrite: true); copied = true; }
                catch (Exception ex) { last = ex; Thread.Sleep(500); }
            }
            // 2. Swap by renaming, retried while the old image may still be mapped. The old exe is never truncated: it is either
            //    still in place or sitting in the backup.
            for (int i = 0; i < 40 && copied && !installed; i++)
            {
                try { Swap(staged, target, backup); installed = true; }
                catch (Exception ex) { last = ex; Thread.Sleep(500); }
            }
        }
        catch (Exception ex) { last = ex; }

        if (installed)
        {
            Log.Info($"[update] installed v{GetCurrentSemVer()} to {target} (previous exe kept as {backup} until the new one has started)");
            TryDelete(FailedMarker(self));
            TryDelete(TimeoutMarker(self));
            try { File.WriteAllText(InstalledMarker(self), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {target}"); } catch { }
        }
        else
        {
            Log.Error($"[update] could not replace {target}, starting the old version", last ?? new IOException("unknown"));
            TryDelete(staged);
            if (!File.Exists(target) && File.Exists(backup))
            {
                try { File.Move(backup, target); Log.Info($"[update] restored {target} from {backup}"); }
                catch (Exception ex) { Log.Error($"[update] could not restore {target} from {backup}", ex); }
            }
            // Leave a marker so the old version does not hand over to this file again on every start (a manual check retries it).
            try { File.WriteAllText(FailedMarker(self), $"{Describe(last)}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} {target}"); } catch { }
        }

        Exception? startError = StartClink(target, restartArgs);
        if (startError != null && installed && File.Exists(backup))
        {
            // The new exe will not even start from there (blocked by App Control or AV): put the previous one back instead of
            // leaving no working Clink, and mark this download failed so the old version does not hand over to it again.
            Log.Error("[update] the new exe did not start, restoring the previous one", startError);
            try
            {
                File.Move(backup, target, overwrite: true);
                Log.Info($"[update] restored {target} from {backup}");
                TryDelete(InstalledMarker(self));
                try { File.WriteAllText(FailedMarker(self), $"the new version did not start: {Describe(startError)}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} {target}"); } catch { }
                startError = StartClink(target, restartArgs);
            }
            catch (Exception ex) { Log.Error($"[update] could not restore {target} from {backup}", ex); }
        }
        if (startError != null) Log.Error("[update] could not restart Clink", startError);
        return 0;
    }

    private static Exception? StartClink(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            foreach (string a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    /// <summary>Puts <paramref name="staged"/> in place of <paramref name="target"/>, keeping the old target as <paramref name="backup"/>. Renames only.</summary>
    private static void Swap(string staged, string target, string backup)
    {
        if (!File.Exists(target)) { File.Move(staged, target); return; }
        try { File.Replace(staged, target, backup, ignoreMetadataErrors: true); }
        catch (IOException)
        {
            // ReplaceFile can refuse on some file systems; two renames do the same, and the backup exists before the target is touched.
            File.Move(target, backup, overwrite: true);
            File.Move(staged, target);
        }
    }

    // ------------------------------------------------------------------ background loop

    /// <summary>
    /// Cleans up after a finished install 30 s after start, then, while <see cref="Settings.AutoUpdate"/> is on, checks the feed
    /// right away and every 6 hours, downloading a newer release automatically. The setting is read before every pass (once a
    /// minute), so switching it off stops the checks without a restart, and <see cref="Wake"/> runs a check at once when it is
    /// turned on. While it is off Clink never contacts the feed on its own (the version label in the footer and Check now still
    /// check on demand).
    /// </summary>
    public void StartBackgroundChecks()
    {
        if (_loop != null || !UpdatesEnabled) return;
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FirstCheckDelay, ct).ConfigureAwait(false);
                // after the delay: right after an update the apply process may still be exiting. Under the gate so it cannot
                // delete a download the check is just verifying or making pending.
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try { CleanupInstalled(); }
                finally { _gate.Release(); }
                if (!_settings.AutoUpdate) Log.Info("updater: AutoUpdate is off, not checking (click the version in the footer to check by hand)");
                DateTime lastCheck = DateTime.MinValue;
                while (!ct.IsCancellationRequested)
                {
                    if (_settings.AutoUpdate && DateTime.UtcNow - lastCheck >= CheckInterval)
                    {
                        _wake.Wait(0);   // a Wake() from before this pass is answered by it
                        lastCheck = DateTime.UtcNow;
                        // One failed pass (timeout after sleep/resume, a full disk...) must not end the checks for the rest of the session.
                        try { await CheckAndMaybeDownloadAsync(ct, manual: false).ConfigureAwait(false); }
                        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn("updater: background check failed: " + ex.Message); }
                    }
                    // Wake() (auto-update just turned on) ends the wait early and makes the next pass check whenever it last did.
                    if (await _wake.WaitAsync(LoopTick, ct).ConfigureAwait(false)) lastCheck = DateTime.MinValue;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Log.Warn("updater: loop stopped: " + ex.Message); }
        }, ct);
    }

    /// <summary>Runs the background loop's next pass now instead of at the end of its 6 h wait (auto-update was just turned on).</summary>
    public void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>Manual check (also downloads when auto-update is on, and retries a download whose install failed). Never throws; null = nothing newer or the check failed (see <see cref="LastError"/>).</summary>
    public async Task<UpdateInfo?> CheckNowAsync()
    {
        if (AppController.IsDev) { LastError = DevModeNote.ToLowerInvariant(); CheckFailed = true; return null; }   // nothing is looked for, downloaded or deleted
        try { return await CheckAndMaybeDownloadAsync(_cts.Token, manual: true).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("updater: manual check failed: " + ex.Message); LastError = "the update check failed (details in the log)"; CheckFailed = true; return null; }
    }

    /// <summary>One pass of the background loop (not manual: the download attempt cap applies), for the unit tests.</summary>
    internal Task<UpdateInfo?> RunBackgroundPassAsync(CancellationToken ct) => CheckAndMaybeDownloadAsync(ct, manual: false);

    /// <summary>
    /// The notification for a version whose download failed <see cref="MaxDownloadAttempts"/> times (<paramref name="tries"/>): fits a tray
    /// balloon (250 characters), the reason shortened to make room for the retry hint.
    /// </summary>
    private UpdateNotice DownloadCapNotice(ReleaseVersion version, int tries)
    {
        string why = (_lastDownloadError ?? "unknown error").ReplaceLineEndings(" ").Trim().TrimEnd('.');
        if (why.Length > 110) why = why[..110] + "…";
        return new UpdateNotice($"download:{version}", "Clink update not downloaded", $"Downloading v{version} failed {tries} times ({why}). {RetryHint}");
    }

    private async Task<UpdateInfo?> CheckAndMaybeDownloadAsync(CancellationToken ct, bool manual)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        UpdateNotice? notice = null;
        try
        {
            // A download from before the channel was switched (or before the running exe changed) may no longer be wanted.
            DropStalePending();
            var info = await CheckAsync(ct, manual).ConfigureAwait(false);
            if (info == null)
            {
                if (CheckFailed) notice = _failNotice;   // set only when this very (background) check found the checks failing for over 3 days
                return null;
            }
            // Already downloaded in this run. Of the same version only the very download the signed manifest describes counts; a pending
            // download newer than the offer still wins (it was itself made from a signed manifest, and an adopted file must pass VerifyDownload's signed check).
            if (PendingVersion is { } pending && pending >= info.Version && ShouldInstall(pending, allowLeaveDev: true) && PendingPath is { } pendingFile && File.Exists(pendingFile)
                && (pending > info.Version || RecordedDownloadIs(info))) return info;
            UpdateAvailable?.Invoke(info);
            if (!_settings.AutoUpdate) return info;

            // Downloaded by an earlier run? Adopt it if it still matches what was verified back then.
            string existing = FileFor(info.Version);
            if (File.Exists(existing))
            {
                // The cheap record check first (no hashing): a file that is not the signed download this offer describes is discarded below
                // whatever markers it has; a .failed marker an older build left beside its unsigned download must not hold the signed one up.
                string? bad = RecordedDownloadIs(info) ? null : "it is not the download the signed manifest describes";
                string? failed = FailedReason(existing);
                if (failed != null && !manual && bad == null)
                {
                    LastError = $"installing v{info.Version} failed earlier ({failed.TrimEnd('.')}). {RetryHint}";
                    Log.Warn($"updater: v{info.Version} is downloaded but installing it failed earlier ({failed}); click the version in the footer to retry");
                    return info;
                }
                if (File.Exists(InstalledMarker(existing)))
                {
                    LastError = $"v{info.Version} was already installed once from {existing}, yet this exe still reports v{VersionDisplay}";
                    Log.Warn($"updater: v{info.Version} was already installed from {existing} but this exe is v{VersionDisplay}; not offering it again");
                    return info;
                }
                bad ??= VerifyDownload(existing, info.Version);
                if (bad == null && info.ExeSize > 0 && new FileInfo(existing).Length != info.ExeSize) bad = "its size differs from the signed manifest's";
                if (bad == null)
                {
                    if (failed != null) { Log.Info($"updater: retrying v{info.Version} after an earlier failure ({failed})"); TryDelete(FailedMarker(existing)); TryDelete(TimeoutMarker(existing)); }
                    return TrySetPending(existing, info.Version, "already downloaded", manual) ? info : null;
                }
                Log.Warn($"updater: discarding {existing}: {bad}");
                TryDelete(existing);
                DeleteMarkers(existing);
                DropStalePending();   // it may have been this run's pending update: no "Update to v…" for a file that is gone
            }

            // The cap keeps the background loop from re-fetching a broken release; a manual check always tries again.
            int tries = _attempts.GetValueOrDefault(info.Version);
            if (tries >= MaxDownloadAttempts && !manual)
            {
                LastError = $"downloading v{info.Version} failed {tries} times ({_lastDownloadError ?? "unknown error"}). {RetryHint}";
                notice = DownloadCapNotice(info.Version, tries);
                return info;
            }
            _attempts[info.Version] = tries + 1;
            try
            {
                string path = await DownloadAsync(info, null, ct).ConfigureAwait(false);
                if (!TrySetPending(path, info.Version, "downloaded and verified", manual)) return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // shutting down; an HttpClient timeout is a failure like any other
            catch (Exception ex)
            {
                _lastDownloadError = DownloadErrorText(ex, info.ExeUrl);   // a fixed text: the exception's message (the log below) may hold what a server sent
                LastError = $"downloading v{info.Version} failed: {_lastDownloadError}";
                Log.Warn($"updater: download of v{info.Version} failed ({tries + 1}/{MaxDownloadAttempts}): {ex.Message}");
                // The attempt that reaches the cap is announced (once per version: the key), not only the passes the cap then refuses.
                if (tries + 1 >= MaxDownloadAttempts) notice = DownloadCapNotice(info.Version, tries + 1);
            }
            return info;
        }
        finally
        {
            _gate.Release();
            // Every pass reports, so a background pass's result reaches the window (and a notice the tray); not one cut short by shutdown.
            if (!ct.IsCancellationRequested)
            {
                try { CheckFinished?.Invoke(notice); } catch (Exception ex) { Log.Warn("updater: CheckFinished handler: " + ex.Message); }
            }
        }
    }

    /// <summary>
    /// Makes a verified download the pending update, unless the channel chosen <em>now</em> no longer wants it (Dev Build was
    /// switched while it downloaded; the check itself ran on the channel of its start). Then nothing becomes pending and false is
    /// returned; the file stays in the updates folder, where the startup scan and <see cref="CleanupInstalled"/> apply the live
    /// channel too (it is adopted without a new download if the channel is switched back, and deleted at the next start otherwise).
    /// </summary>
    internal bool TrySetPending(string path, ReleaseVersion version, string how, bool manual)
    {
        lock (_pendingLock)
        {
            if (!ShouldInstall(version, allowLeaveDev: manual))
            {
                Log.Info($"updater: v{version} {how}, but the update channel changed meanwhile ({(_settings.UseDevBuilds ? "dev builds" : "releases")}); not offering it");
                return false;
            }
            PendingPath = path;
            PendingVersion = version;
        }
        // Accepted by this exe now (a download adopted from an earlier run included), so the startup scan may install it (LeaveDevAllowedAtStart).
        string self = Current.ToString();
        if (RecFrom != self) { RecFrom = self; _settings.Save(); }
        // A problem reported at startup (an earlier failed install) is out of date now; keep only what still blocks installing.
        string? blocked = InstallProblemFor(Environment.ProcessPath ?? "");
        InstallProblem = blocked == null ? null : $"Update to v{version} is downloaded but {blocked}; {HowToInstallByHand}.";
        Log.Info($"updater: v{version} {how}: {path}");
        try { UpdateReady?.Invoke(version); } catch (Exception ex) { Log.Warn("updater: UpdateReady handler: " + ex.Message); }
        return true;
    }

    /// <summary>
    /// Forgets <see cref="PendingPath"/> if the channel chosen now or the running exe no longer wants it (raising <see cref="PendingCleared"/>),
    /// so it can never be installed by <c>Update to v…</c>. True if something was dropped. The file stays (see <see cref="TrySetPending"/>).
    /// </summary>
    public bool DropStalePending()
    {
        ReleaseVersion dropped;
        lock (_pendingLock)
        {
            if (PendingVersion is not { } v || (ShouldInstall(v, allowLeaveDev: true) && PendingPath is { } p && File.Exists(p))) return false;
            dropped = v;
            PendingPath = null;
            PendingVersion = null;
        }
        Log.Info($"updater: v{dropped} is no longer wanted on the {(_settings.UseDevBuilds ? "dev" : "release")} channel; not offering it");
        try { PendingCleared?.Invoke(); } catch (Exception ex) { Log.Warn("updater: PendingCleared handler: " + ex.Message); }
        return true;
    }

    /// <summary>
    /// Removes the previous exe kept by <see cref="RunApplyUpdate"/> (this process started, so it is no longer needed), downloads
    /// that are not newer than the running exe, stale partials and orphaned markers.
    /// </summary>
    private void CleanupInstalled()
    {
        if (AppController.IsDev) return;   // the folder is the real Clink's, and what it deletes there is its pending update
        try
        {
            string? self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self)) { TryDelete(self + ".bak"); TryDelete(self + ".new"); }
            CleanupUpdatesFolder();
        }
        catch (Exception ex) { Log.Warn("updater: cleanup: " + ex.Message); }
    }

    /// <summary>The updates-folder half of <see cref="CleanupInstalled"/>: this packaging's folder (<see cref="UpdatesDir"/>) in full, the other one's only for applied files (<see cref="CleanupOtherFolder"/>).</summary>
    internal void CleanupUpdatesFolder()
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (string file in Directory.EnumerateFiles(UpdatesDir, "Clink-*.exe"))
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var v = VersionFromFile(file);
                if (v == null || !ShouldInstall(v.Value, allowLeaveDev: LeaveDevAllowedAtStart(v.Value))) { TryDelete(file); DeleteMarkers(file); }
            }
            foreach (string stale in Directory.EnumerateFiles(UpdatesDir, "*.partial")) TryDelete(stale);
            foreach (string marker in Directory.EnumerateFiles(UpdatesDir, "*.failed"))
                if (!File.Exists(marker[..^".failed".Length])) TryDelete(marker);
            foreach (string marker in Directory.EnumerateFiles(UpdatesDir, "*.timeout"))
                if (!File.Exists(marker[..^".timeout".Length])) TryDelete(marker);
            foreach (string marker in Directory.EnumerateFiles(UpdatesDir, "*.installed"))
                if (!File.Exists(marker[..^".installed".Length])) TryDelete(marker);
        }
        catch (Exception ex) { Log.Warn("updater: cleanup: " + ex.Message); }
        finally { CleanupOtherFolder(); }   // also when this packaging's folder does not exist
    }

    /// <summary>
    /// The other packaging's folder keeps files nobody will ever clean once a copy changes flavour (a Framework copy that fell back to the
    /// self-contained exe leaves its applied updates\fx\ files behind). Only what was already applied is removed: a Clink-*.exe that has an
    /// <c>.installed</c> marker, plus that marker. Never recursive, and a file without a marker (a pending download of the other flavour) is not touched.
    /// </summary>
    private void CleanupOtherFolder()
    {
        try
        {
            string other = UpdatesDirFor(OwnPackaging == Packaging.Framework ? Packaging.SelfContained : Packaging.Framework);
            if (!Directory.Exists(other)) return;
            foreach (string file in Directory.EnumerateFiles(other, "Clink-*.exe"))
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(InstalledMarker(file))) continue;
                TryDelete(file);
                DeleteMarkers(file);
            }
        }
        catch (Exception ex) { Log.Warn("updater: cleanup of the other flavour's folder: " + ex.Message); }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }
}
