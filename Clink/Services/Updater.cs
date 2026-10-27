using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clink.Services;

/// <summary>
/// A newer release (or, on the dev channel, dev build) found on GitHub or in the signed feed. <see cref="ShaUrl"/> is never null: a GitHub
/// release without <c>Clink.exe.sha256</c> is not offered. A feed result carries the hash inline in <see cref="Sha256"/> (lowercase hex)
/// and an empty <see cref="ShaUrl"/>; the download then fetches no hash file.
/// </summary>
internal sealed record UpdateInfo(ReleaseVersion Version, string Tag, string ExeUrl, long ExeSize, string ShaUrl, string HtmlUrl, string Notes, string? Sha256 = null);

/// <summary>
/// Self-update from GitHub Releases. Polls <c>/releases/latest</c> for a newer <c>Clink.exe</c> asset (or, with
/// <see cref="Settings.UseDevBuilds"/>, the newest of the recent releases and the <c>X.Y.Z-dev.N</c> pre-releases published from the
/// dev branch; <see cref="ShouldInstall(ReleaseVersion, ReleaseVersion, bool, bool)"/> says what counts as newer), downloads and
/// verifies it into <c>%LOCALAPPDATA%\Clink\updates</c>, and swaps it over the running exe either on the next start
/// (<see cref="TryGetPendingUpdate"/>) or right away (<see cref="LaunchApply"/>). The swap itself is done by the
/// downloaded exe running with <c>--apply-update</c> (<see cref="RunApplyUpdate"/>), so the old file is never
/// written while it is still executing.
/// <para>
/// What is verified: the download must match the release's <c>Clink.exe.sha256</c> (guards against a corrupted transfer),
/// and must report the version its tag promises (guards against a mis-built release that would install forever). The verified
/// hash is remembered in settings and checked again before the file is installed, so a file changed in the updates folder
/// since the download is thrown away. None of this authenticates the publisher; that needs code signing (see ROADMAP).
/// </para>
/// </summary>
internal sealed class Updater : IDisposable
{
    private const string ExeAssetName = "Clink.exe";
    private const string ShaAssetName = "Clink.exe.sha256";
    private const string MetadataKey = "UpdateRepo";
    private const int MaxDownloadAttempts = 3;              // per version, per process; a broken release should not be re-fetched every cycle
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan LoopTick = TimeSpan.FromMinutes(1);       // how soon the background loop notices AutoUpdate being switched
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);   // no bytes for this long = the download is stalled
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Regex s_hex64 = new("(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.Compiled);
    private static readonly Regex s_repo = new("^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9._-]+$", RegexOptions.Compiled);   // "owner/repo", safe to put in a URL

    private readonly Settings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);        // one check/download at a time
    private readonly SemaphoreSlim _wake = new(0, 1);        // released by Wake(): cuts the background loop's wait short
    private readonly Dictionary<ReleaseVersion, int> _attempts = new();
    private readonly object _pendingLock = new();            // PendingPath/PendingVersion: set by the check, dropped by the UI too
    private string? _lastDownloadError;
    private Task? _loop;
    private bool _loggedBadRepo;

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

    public Updater(Settings settings) : this(settings, GetCurrentSemVer()) { }

    /// <summary>With the running version given (the unit tests pretend to be a particular build).</summary>
    internal Updater(Settings settings, ReleaseVersion current)
    {
        _settings = settings;
        Current = current;
        // The window shows this in the Updates card ("Last check failed: updates are off in dev mode"), as the status of a --dev instance.
        if (AppController.IsDev) { LastError = DevModeNote.ToLowerInvariant(); CheckFailed = true; }
    }

    /// <summary>Where updates are downloaded instead of <c>%LOCALAPPDATA%\Clink\updates</c> (the real Clink's, shared by every Clink exe on the machine): set by the unit tests, null otherwise.</summary>
    internal static string? UpdatesDirOverride { get; set; }
    public static string UpdatesDir => UpdatesDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink", "updates");

    /// <summary>What a --dev instance says when asked to look for, download or install an update.</summary>
    public const string DevModeNote = "Updates are off in dev mode";

    /// <summary>
    /// "owner/repo" to check, from settings or the UpdateRepo assembly metadata; null = updates disabled. Always null in a
    /// <c>--dev</c> instance: it would download into the real Clink's updates folder, delete the other exes there (the real Clink's
    /// pending update among them), and restart as a normal Clink.
    /// </summary>
    public string? Repo
    {
        get
        {
            if (AppController.IsDev) return null;
            string? repo = SettingsRepo ?? AssemblyRepo?.Trim().Trim('/');
            return string.IsNullOrWhiteSpace(repo) ? null : repo;
        }
    }

    /// <summary>True when settings.json points the updater somewhere other than the repository baked into this exe.</summary>
    public bool RepoOverridden => SettingsRepo != null;

    /// <summary>The settings.json override, or null when absent or not a plain "owner/repo" (then it is ignored and logged).</summary>
    private string? SettingsRepo
    {
        get
        {
            string? repo = _settings.UpdateRepo?.Trim().Trim('/');
            if (string.IsNullOrEmpty(repo)) return null;
            if (s_repo.IsMatch(repo)) return repo;
            if (!_loggedBadRepo) { _loggedBadRepo = true; Log.Warn($"updater: ignoring UpdateRepo '{_settings.UpdateRepo}' in settings.json (expected owner/repo)"); }
            return null;
        }
    }

    private static Assembly EntryAssembly => Assembly.GetEntryAssembly() ?? typeof(Updater).Assembly;

    private static string? AssemblyRepo =>
        EntryAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == MetadataKey)?.Value;

    private static string? Metadata(string key) =>
        EntryAssembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    /// <summary>Replaces the network for the update check, the hash file and the exe download: set by the unit tests, null otherwise.</summary>
    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HttpOverride { get; set; }
    /// <summary>Stands in for the UpdateFeed / UpdateFeedPublicKey assembly metadata in the unit tests; null otherwise.</summary>
    internal static (string Url, string PublicKey)? FeedOverride { get; set; }

    private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead) =>
        HttpOverride != null ? HttpOverride(req, ct) : s_http.SendAsync(req, option, ct);

    /// <summary>
    /// The signed feed to try before GitHub: its base URL (https, ending in '/') and public key, only when BOTH the UpdateFeed and
    /// UpdateFeedPublicKey metadata are non-empty; null otherwise (local builds: GitHub only, exactly as before).
    /// </summary>
    internal static (string BaseUrl, string PublicKey)? FeedConfig
    {
        get
        {
            string? url = FeedOverride?.Url ?? Metadata("UpdateFeed"), key = FeedOverride?.PublicKey ?? Metadata("UpdateFeedPublicKey");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return null;
            url = url.Trim();
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            if (!url.EndsWith('/')) url += "/";
            return (url, key.Trim());
        }
    }

    /// <summary>Logs once at startup where updates come from, so a redirected settings.json is visible in the log.</summary>
    public void LogSource()
    {
        if (AppController.IsDev) { Log.Info("updater: off in a --dev instance"); return; }
        string? repo = Repo;
        if (repo == null) { Log.Info("updater: no UpdateRepo configured, updates disabled"); return; }
        string builtIn = string.IsNullOrWhiteSpace(AssemblyRepo) ? "none" : AssemblyRepo.Trim().Trim('/');
        Log.Info(RepoOverridden
            ? $"updater: updates come from github.com/{repo} (UpdateRepo in settings.json overrides the built-in {builtIn})"
            : $"updater: updates come from github.com/{repo}");
        if (FeedConfig is { } feed) Log.Info($"updater: the signed feed {feed.BaseUrl} is tried first, GitHub is the fallback");
    }

    /// <summary>GET for the feed: the body, or an exception for a network error or a non-success status.</summary>
    private async Task<byte[]> FetchFeedAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req);
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode} for {url}");
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
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
        string.Equals(_settings.PendingUpdateFrom, Current.ToString(), StringComparison.Ordinal)
        && string.Equals(_settings.PendingUpdateVersion, file.ToString(), StringComparison.Ordinal);

    internal static string FileFor(ReleaseVersion v) => Path.Combine(UpdatesDir, $"Clink-{v}.exe");

    /// <summary>
    /// Written by <see cref="LaunchApply"/> before the hand-over (so an apply process that never finishes counts as failed) and by
    /// <see cref="RunApplyUpdate"/> when the swap failed; deleted by a successful swap. Otherwise it stays until a newer download
    /// replaces the file or a manual check retries it.
    /// </summary>
    private static string FailedMarker(string exePath) => exePath + ".failed";

    /// <summary>Written by <see cref="RunApplyUpdate"/> after a successful swap, so the file is never installed a second time (even if the new exe reports an unexpected version).</summary>
    private static string InstalledMarker(string exePath) => exePath + ".installed";

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
        return name.StartsWith("Clink-", StringComparison.OrdinalIgnoreCase) ? ReleaseVersion.Parse(name[6..]) : null;
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

    /// <summary>
    /// Asks GitHub for the latest release (stable channel) or the newest of the 30 most recent releases and dev builds
    /// (<see cref="Settings.UseDevBuilds"/>). Returns info only if <see cref="ShouldInstall(ReleaseVersion, ReleaseVersion, bool, bool)"/>
    /// accepts it; <paramref name="manual"/> allows going from a dev build back to an older release. Never throws.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct, bool manual = false)
    {
        string? repo = Repo;
        if (repo == null) { LastError = "updates are not configured in this build"; CheckFailed = true; return null; }
        bool dev = _settings.UseDevBuilds;   // one channel for the whole check, even if the box is ticked meanwhile
        try
        {
            // The signed feed first. Any problem (and a feed that does not offer anything newer) is logged and GitHub is asked as before;
            // nothing from an unverified manifest is ever used.
            if (FeedConfig is { } feed)
            {
                var (feedInfo, feedProblem) = await UpdateFeed.TryGetAsync(FetchFeedAsync, feed.BaseUrl, feed.PublicKey, dev, ct).ConfigureAwait(false);
                if (feedInfo == null)
                    Log.Warn($"updater: feed unusable ({feedProblem}); falling back to GitHub");
                else if (!ShouldInstall(feedInfo.Version, Current, dev, allowLeaveDev: manual))
                    Log.Info($"updater: feed offers {feedInfo.Tag}, not newer than v{VersionDisplay}; falling back to GitHub");
                else
                {
                    LastError = null;
                    Log.Info($"updater: v{feedInfo.Version} available ({feedInfo.ExeSize / 1024} KB) from the signed feed");
                    return feedInfo;
                }
            }
            string url = dev ? $"https://api.github.com/repos/{repo}/releases?per_page=30" : $"https://api.github.com/repos/{repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(req);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // The whole (small) answer is buffered inside SendAsync, so HttpClient.Timeout also covers a body that stalls.
            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = (int)resp.StatusCode == 404 ? $"no releases found for {repo}" : $"GitHub answered HTTP {(int)resp.StatusCode}";
                Log.Warn($"updater: {LastError}: {Truncate(body)}");
                return null;
            }
            string? problem;
            var info = dev ? PickRelease(body, true, out problem) : ParseRelease(body, false, out problem);
            if (info == null) { LastError = problem ?? "latest release is unusable"; Log.Warn("updater: " + LastError); return null; }
            LastError = null;
            if (!ShouldInstall(info.Version, Current, dev, allowLeaveDev: manual))
            {
                if (!dev && Current.IsDevBuild && info.Version < Current)
                    Log.Info($"updater: running dev build v{Current}; latest release v{info.Version} is older — Check for updates switches back");
                else
                    Log.Info($"updater: up to date (latest {info.Tag}, running v{VersionDisplay})");
                return null;
            }
            Log.Info($"updater: v{info.Version} available ({info.ExeSize / 1024} KB) from github.com/{repo}");
            return info;
        }
        catch (Exception ex)
        {
            // HttpClient.Timeout surfaces as a cancellation too; only a cancelled token means Clink is shutting down.
            LastError = ex is OperationCanceledException
                ? ct.IsCancellationRequested ? "cancelled" : $"GitHub did not answer within {s_http.Timeout.TotalSeconds:0} s"
                : Describe(ex);
            Log.Warn("updater: check failed: " + ex.Message);
            return null;
        }
        finally
        {
            CheckFailed = LastError != null;   // every way out of the try sets LastError: null on success, the reason otherwise
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Save();
        }
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Clink", CurrentVersion.ToString(3)));
    }

    /// <summary>Answer of <c>/releases/latest</c> as an <see cref="UpdateInfo"/>, or null with <paramref name="problem"/> set (pre-release/draft: problem stays null).</summary>
    internal static UpdateInfo? ParseRelease(string json, bool devChannel, out string? problem)
    {
        problem = null;
        using var doc = JsonDocument.Parse(json);
        return ParseRelease(doc.RootElement, devChannel, out problem);
    }

    /// <summary>
    /// The newest usable entry of a <c>/releases</c> list. GitHub lists by creation date, not by version, so the maximum is
    /// computed. Drafts, beta pre-releases, dev-looking tags not flagged as pre-release and entries without both assets are skipped.
    /// Null with <paramref name="problem"/> set when nothing is usable.
    /// </summary>
    internal static UpdateInfo? PickRelease(string listJson, bool devChannel, out string? problem)
    {
        problem = null;
        using var doc = JsonDocument.Parse(listJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) { problem = "unexpected answer from GitHub"; return null; }
        UpdateInfo? best = null;
        string? lastProblem = null;   // e.g. a missing sha256 asset: says why nothing was usable
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var info = ParseRelease(entry, devChannel, out string? entryProblem);
            if (entryProblem != null) lastProblem = entryProblem;
            if (info != null && (best == null || info.Version > best.Version)) best = info;
        }
        if (best == null) problem = "no usable release or dev build found" + (lastProblem != null ? $" ({lastProblem})" : "");
        return best;
    }

    /// <summary>
    /// One release object as an <see cref="UpdateInfo"/>, or null (with <paramref name="problem"/> when that is worth telling the user).
    /// Stable channel: pre-releases are refused. Dev channel: a plain X.Y.Z release or a pre-release tagged X.Y.Z-dev.N is accepted.
    /// </summary>
    internal static UpdateInfo? ParseRelease(JsonElement root, bool devChannel, out string? problem)
    {
        problem = null;
        if (root.ValueKind != JsonValueKind.Object) { problem = "unexpected answer from GitHub"; return null; }
        if (Flag(root, "draft")) return null;
        bool prerelease = Flag(root, "prerelease");
        if (prerelease && !devChannel) return null;
        string tag = Str(root, "tag_name");
        var parsed = ReleaseVersion.Parse(tag);
        if (parsed == null) { problem = $"cannot parse release tag '{tag}'"; return null; }
        var version = parsed.Value;
        if (devChannel)
        {
            // A dev build must be flagged pre-release (so /releases/latest and the stable channel never see it); a release must be a plain X.Y.Z.
            if (prerelease ? !version.IsDevBuild : version.IsPrerelease) return null;
        }
        string? exeUrl = null, shaUrl = null; long exeSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = Str(a, "name");
                if (name.Equals(ExeAssetName, StringComparison.Ordinal))
                {
                    exeUrl = Str(a, "browser_download_url");
                    exeSize = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                }
                else if (name.Equals(ShaAssetName, StringComparison.Ordinal)) shaUrl = Str(a, "browser_download_url");
            }
        }
        if (string.IsNullOrEmpty(exeUrl)) { problem = $"release {tag} has no {ExeAssetName} asset"; return null; }
        // Fail closed: without the published hash nothing could tell a corrupted download from a good one.
        if (string.IsNullOrEmpty(shaUrl)) { problem = $"release {tag} has no {ShaAssetName} asset, refusing it"; return null; }
        return new UpdateInfo(version, tag, exeUrl, exeSize, shaUrl, Str(root, "html_url"), Str(root, "body"));

        static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    // ------------------------------------------------------------------ download

    /// <summary>
    /// Downloads and verifies the release exe; returns the path of <c>Clink-{version}.exe</c> in the updates folder and records
    /// the verified SHA-256 in settings for <see cref="TryGetPendingUpdate"/>. Throws on any failure.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (AppController.IsDev) throw new InvalidOperationException(DevModeNote);
        Directory.CreateDirectory(UpdatesDir);
        string final = FileFor(info.Version);
        string partial = final + ".partial";
        IsDownloading = true;
        try
        {
            // The hash first: if it cannot be fetched there is no point transferring 30 MB.
            string expected = info.Sha256 ?? await FetchShaAsync(info.ShaUrl, ct).ConfigureAwait(false);
            long done = 0;
            using (var req = new HttpRequestMessage(HttpMethod.Get, info.ExeUrl))
            {
                AddHeaders(req);
                using var resp = await SendAsync(req, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? info.ExeSize;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buf = new byte[1 << 16];
                // HttpClient.Timeout only covers the headers once the body is streamed; a transfer that goes quiet must not hold
                // the updater (and its gate) for the rest of the session, so every read gets its own inactivity deadline.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    int n;
                    idle.CancelAfter(ReadTimeout);
                    while ((n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false)) > 0)
                    {
                        idle.CancelAfter(ReadTimeout);
                        await dst.WriteAsync(buf.AsMemory(0, n), idle.Token).ConfigureAwait(false);
                        done += n;
                        if (total > 0) progress?.Report(Math.Min(1.0, done / (double)total));
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"download stalled (no data for {ReadTimeout.TotalSeconds:0} s after {done / 1024} KB)");
                }
            }

            long length = new FileInfo(partial).Length;
            if (info.ExeSize > 0 && length != info.ExeSize) throw new InvalidDataException($"size mismatch: got {length} bytes, release says {info.ExeSize}");
            if (!LooksLikeExe(partial)) throw new InvalidDataException("downloaded file is not a Windows executable");
            string actual = Sha256Hex(partial);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 mismatch");
            string? wrongVersion = CheckEmbeddedVersion(partial, info.Version);
            if (wrongVersion != null) throw new InvalidDataException($"release {info.Tag} is unusable: {wrongVersion}");
            progress?.Report(1.0);

            File.Move(partial, final, overwrite: true);
            _settings.PendingUpdateVersion = info.Version.ToString();
            _settings.PendingUpdateSha256 = actual;
            _settings.PendingUpdateFrom = Current.ToString();   // see LeaveDevAllowedAtStart()
            _settings.Save();
            TryDelete(FailedMarker(final));
            TryDelete(InstalledMarker(final));
            foreach (string other in Directory.EnumerateFiles(UpdatesDir, "Clink-*.exe"))
            {
                if (!other.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFullPath(other), Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase)) continue;
                TryDelete(other);
                TryDelete(FailedMarker(other));
                TryDelete(InstalledMarker(other));
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

    private async Task<string> FetchShaAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req);
        using var resp = await SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var m = s_hex64.Match(text);
        if (!m.Success) throw new InvalidDataException("sha256 file contains no SHA-256 hash");
        return m.Value;
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
    private string? VerifyDownload(string file, ReleaseVersion version)
    {
        if (!LooksLikeExe(file)) return "it is not a Windows executable";
        if (!string.Equals(_settings.PendingUpdateVersion, version.ToString(),StringComparison.Ordinal) || string.IsNullOrEmpty(_settings.PendingUpdateSha256))
            return "there is no record of a verified download of this version";
        string actual;
        try { actual = Sha256Hex(file); }
        catch (Exception ex) { return "it cannot be read: " + ex.Message; }
        if (!string.Equals(actual, _settings.PendingUpdateSha256, StringComparison.OrdinalIgnoreCase)) return "its SHA-256 no longer matches the verified download";
        return CheckEmbeddedVersion(file, version);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Warn($"updater: could not delete {path}: {ex.Message}"); }
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;

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
        if (IsElevated()) return "Clink is running as administrator and does not install updates from an elevated process";
        string dir = Path.GetDirectoryName(targetExe) ?? "";
        try
        {
            string probe = Path.Combine(dir, $"Clink.write-test.{Environment.ProcessId}");
            using var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return null;
        }
        catch (Exception) { return $"Clink.exe's folder ({dir}) is not writable"; }
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Newest downloaded <c>Clink-&lt;ver&gt;.exe</c> that is newer than the running exe and still matches what was verified at
    /// download time. False (and nothing is run) when this build has no update repository, the file was already installed from,
    /// failed to install earlier, or cannot be installed by this process; <see cref="InstallProblem"/> then carries the message
    /// for the user where there is something they can do about it.
    /// </summary>
    public bool TryGetPendingUpdate(out string path, out ReleaseVersion version)
    {
        path = ""; version = Current; InstallProblem = null;
        try
        {
            // A build without UpdateRepo never downloaded anything; the updates folder is shared by every Clink exe on the machine.
            if (Repo == null || !Directory.Exists(UpdatesDir)) return false;
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
            string? reason = FailedReason(best);
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
                TryDelete(FailedMarker(best));
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
        bool appControl = ex.Message.Contains("Application Control", StringComparison.OrdinalIgnoreCase);
        // Kept short: this text comes back at every start inside a longer message, and tray balloons stop at 250 characters.
        string why = appControl
            ? "Smart App Control blocked it (Clink is not code-signed yet). Turn it off in Windows Security › App & browser control, or build Clink from source"
            : Describe(ex);
        InstallProblem = $"Update to v{version} could not start: {why.TrimEnd('.')}. {RetryHint}";
        Log.Warn("updater: " + InstallProblem);
        // Same marker the apply process writes: this file is not offered again at every start (a manual check retries it).
        try { File.WriteAllText(FailedMarker(pendingExe), $"{why}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss} launch failed"); } catch { }
    }

    /// <summary>
    /// Starts the downloaded exe so it replaces <paramref name="targetExePath"/> once this process has exited and then restarts
    /// it with <paramref name="restartArgs"/> (the current command line, so a login start stays in the tray). The caller then exits.
    /// </summary>
    public static void LaunchApply(string newExePath, string targetExePath, IEnumerable<string> restartArgs)
    {
        if (AppController.IsDev) throw new InvalidOperationException(DevModeNote);
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
    /// Cleans up after a finished install 30 s after start, then, while <see cref="Settings.AutoUpdate"/> is on, checks GitHub
    /// right away and every 6 hours, downloading a newer release automatically. The setting is read before every pass (once a
    /// minute), so switching it off stops the checks without a restart, and <see cref="Wake"/> runs a check at once when it is
    /// turned on. While it is off Clink never contacts GitHub on its own (the version label in the footer and Check now still
    /// check on demand).
    /// </summary>
    public void StartBackgroundChecks()
    {
        if (_loop != null || Repo == null) return;
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
                if (!_settings.AutoUpdate) Log.Info("updater: AutoUpdate is off, not checking GitHub (click the version in the footer to check by hand)");
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
        catch (Exception ex) { Log.Warn("updater: manual check failed: " + ex.Message); LastError = ex.Message; return null; }
    }

    private async Task<UpdateInfo?> CheckAndMaybeDownloadAsync(CancellationToken ct, bool manual)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A download from before the channel was switched (or before the running exe changed) may no longer be wanted.
            DropStalePending();
            var info = await CheckAsync(ct, manual).ConfigureAwait(false);
            if (info == null) return null;
            if (PendingVersion is { } pending && pending >= info.Version && ShouldInstall(pending, allowLeaveDev: true) && PendingPath is { } pendingFile && File.Exists(pendingFile)) return info;   // already downloaded in this run
            UpdateAvailable?.Invoke(info);
            if (!_settings.AutoUpdate) return info;

            // Downloaded by an earlier run? Adopt it if it still matches what was verified back then.
            string existing = FileFor(info.Version);
            if (File.Exists(existing))
            {
                string? failed = FailedReason(existing);
                if (failed != null && !manual)
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
                string? bad = VerifyDownload(existing, info.Version);
                if (bad == null && info.ExeSize > 0 && new FileInfo(existing).Length != info.ExeSize) bad = "its size differs from the release asset";
                if (bad == null)
                {
                    if (failed != null) { Log.Info($"updater: retrying v{info.Version} after an earlier failure ({failed})"); TryDelete(FailedMarker(existing)); }
                    return TrySetPending(existing, info.Version, "already downloaded", manual) ? info : null;
                }
                Log.Warn($"updater: discarding {existing}: {bad}");
                TryDelete(existing);
                TryDelete(FailedMarker(existing));
                TryDelete(InstalledMarker(existing));
            }

            // The cap keeps the background loop from re-fetching a broken release; a manual check always tries again.
            int tries = _attempts.GetValueOrDefault(info.Version);
            if (tries >= MaxDownloadAttempts && !manual)
            {
                LastError = $"downloading v{info.Version} failed {tries} times ({_lastDownloadError ?? "unknown error"}). {RetryHint}";
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
                _lastDownloadError = ex is OperationCanceledException ? $"no answer within {s_http.Timeout.TotalSeconds:0} s" : Describe(ex);
                LastError = $"downloading v{info.Version} failed: {_lastDownloadError}";
                Log.Warn($"updater: download of v{info.Version} failed ({tries + 1}/{MaxDownloadAttempts}): {ex.Message}");
            }
            return info;
        }
        finally { _gate.Release(); }
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
        if (_settings.PendingUpdateFrom != self) { _settings.PendingUpdateFrom = self; _settings.Save(); }
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
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (string file in Directory.EnumerateFiles(UpdatesDir, "Clink-*.exe"))
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var v = VersionFromFile(file);
                if (v == null || !ShouldInstall(v.Value, allowLeaveDev: LeaveDevAllowedAtStart(v.Value))) { TryDelete(file); TryDelete(FailedMarker(file)); TryDelete(InstalledMarker(file)); }
            }
            foreach (string stale in Directory.EnumerateFiles(UpdatesDir, "*.partial")) TryDelete(stale);
            foreach (string marker in Directory.EnumerateFiles(UpdatesDir, "*.failed"))
                if (!File.Exists(marker[..^".failed".Length])) TryDelete(marker);
            foreach (string marker in Directory.EnumerateFiles(UpdatesDir, "*.installed"))
                if (!File.Exists(marker[..^".installed".Length])) TryDelete(marker);
        }
        catch (Exception ex) { Log.Warn("updater: cleanup: " + ex.Message); }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
    }
}
