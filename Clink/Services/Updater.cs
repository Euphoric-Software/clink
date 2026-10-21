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

/// <summary>A newer release found on GitHub. <see cref="ShaUrl"/> is never null: a release without <c>Clink.exe.sha256</c> is not offered.</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string ExeUrl, long ExeSize, string ShaUrl, string HtmlUrl, string Notes);

/// <summary>
/// Self-update from GitHub Releases. Polls <c>/releases/latest</c> for a newer <c>Clink.exe</c> asset, downloads and
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
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);   // no bytes for this long = the download is stalled
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Regex s_hex64 = new("(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.Compiled);
    private static readonly Regex s_repo = new("^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9._-]+$", RegexOptions.Compiled);   // "owner/repo", safe to put in a URL

    private readonly Settings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);        // one check/download at a time
    private readonly Dictionary<Version, int> _attempts = new();
    private Task? _loop;
    private bool _loggedBadRepo;

    /// <summary>Version of the running exe, always three components (major.minor.build).</summary>
    public Version CurrentVersion { get; }
    /// <summary>Pre-release suffix of the running exe ("beta.1" for 0.2.0-beta.1), null for a release build.</summary>
    public string? CurrentPrerelease { get; }
    public bool IsPrerelease => CurrentPrerelease != null;
    /// <summary>"0.2.0" or "0.2.0-beta.1": what to show the user.</summary>
    public string VersionDisplay => Format((CurrentVersion, CurrentPrerelease));
    /// <summary>Path of a downloaded, verified newer exe (null until <see cref="UpdateReady"/> fired).</summary>
    public string? PendingPath { get; private set; }
    public Version? PendingVersion { get; private set; }
    /// <summary>Why the last check returned nothing, or null if it simply found no newer release.</summary>
    public string? LastError { get; private set; }
    /// <summary>True while <see cref="DownloadAsync"/> is transferring the exe (a manual check waits for it).</summary>
    public bool IsDownloading { get; private set; }
    /// <summary>Set by <see cref="TryGetPendingUpdate"/> when a downloaded update could not be installed: a message for the user, or null.</summary>
    public string? InstallProblem { get; private set; }

    /// <summary>A newer release exists (raised on a background thread, before any download).</summary>
    public event Action<UpdateInfo>? UpdateAvailable;
    /// <summary>A newer exe is downloaded and verified (raised on a background thread).</summary>
    public event Action<Version>? UpdateReady;

    public Updater(Settings settings)
    {
        _settings = settings;
        var current = GetCurrentSemVer();
        CurrentVersion = current.Version;
        CurrentPrerelease = current.Prerelease;
    }

    public static string UpdatesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink", "updates");

    /// <summary>"owner/repo" to check, from settings or the UpdateRepo assembly metadata; null = updates disabled.</summary>
    public string? Repo
    {
        get
        {
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

    /// <summary>Logs once at startup where updates come from, so a redirected settings.json is visible in the log.</summary>
    public void LogSource()
    {
        string? repo = Repo;
        if (repo == null) { Log.Info("updater: no UpdateRepo configured, updates disabled"); return; }
        string builtIn = string.IsNullOrWhiteSpace(AssemblyRepo) ? "none" : AssemblyRepo.Trim().Trim('/');
        Log.Info(RepoOverridden
            ? $"updater: updates come from github.com/{repo} (UpdateRepo in settings.json overrides the built-in {builtIn})"
            : $"updater: updates come from github.com/{repo}");
    }

    // ------------------------------------------------------------------ versions

    private static (Version Version, string? Prerelease) GetCurrentSemVer()
    {
        var asm = EntryAssembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return ParseSemVer(info) ?? (Normalize(asm.GetName().Version ?? new Version(0, 0, 0)), null);
    }

    /// <summary>"v1.2.3", "1.2.3+abc123" → (1.2.3, null); "1.2.3-beta.1+abc" → (1.2.3, "beta.1"); null if not a version.</summary>
    private static (Version Version, string? Prerelease)? ParseSemVer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        int cut = s.IndexOfAny(new[] { '+', ' ' });
        if (cut >= 0) s = s[..cut];
        string? pre = null;
        int dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; if (pre.Length == 0) pre = null; }
        return Version.TryParse(s, out var v) ? (Normalize(v), pre) : null;
    }

    private static Version? ParseVersion(string? text) => ParseSemVer(text)?.Version;

    private static Version Normalize(Version v) => new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static string Format((Version Version, string? Prerelease) v) => v.Prerelease == null ? v.Version.ToString(3) : $"{v.Version.ToString(3)}-{v.Prerelease}";

    /// <summary>True if a release numbered <paramref name="v"/> should replace the running exe. A release with the same number replaces a pre-release build of it.</summary>
    private bool IsNewer(Version v) => v > CurrentVersion || (v == CurrentVersion && IsPrerelease);

    private static string FileFor(Version v) => Path.Combine(UpdatesDir, $"Clink-{v.ToString(3)}.exe");

    /// <summary>Written by <see cref="RunApplyUpdate"/> when the swap failed. Stays until a newer download replaces the file or a manual check retries it.</summary>
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

    private static Version? VersionFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("Clink-", StringComparison.OrdinalIgnoreCase) ? ParseVersion(name[6..]) : null;
    }

    /// <summary>Version in an exe's version resource (ProductVersion = InformationalVersion, else FileVersion); null if unreadable.</summary>
    private static (Version Version, string? Prerelease)? EmbeddedVersion(string path)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            return ParseSemVer(fvi.ProductVersion) ?? ParseSemVer(fvi.FileVersion);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ check

    /// <summary>Asks GitHub for the latest release. Returns info only if it is newer than the running version; never throws.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        string? repo = Repo;
        if (repo == null) { LastError = "updates are not configured in this build"; return null; }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            AddHeaders(req);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = (int)resp.StatusCode == 404 ? $"no releases found for {repo}" : $"GitHub answered HTTP {(int)resp.StatusCode}";
                Log.Warn($"updater: {LastError}: {Truncate(body)}");
                return null;
            }
            var info = ParseRelease(body, out string? problem);
            if (info == null) { LastError = problem ?? "latest release is unusable"; Log.Warn("updater: " + LastError); return null; }
            LastError = null;
            if (!IsNewer(info.Version)) { Log.Info($"updater: up to date (latest {info.Tag}, running v{VersionDisplay})"); return null; }
            Log.Info($"updater: v{info.Version} available ({info.ExeSize / 1024} KB) from github.com/{repo}");
            return info;
        }
        catch (Exception ex)
        {
            LastError = ex is OperationCanceledException ? "cancelled" : ex.Message;
            Log.Warn("updater: check failed: " + ex.Message);
            return null;
        }
        finally
        {
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Save();
        }
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Clink", CurrentVersion.ToString(3)));
    }

    /// <summary>Latest release as an <see cref="UpdateInfo"/>, or null with <paramref name="problem"/> set (pre-release/draft: problem stays null).</summary>
    private static UpdateInfo? ParseRelease(string json, out string? problem)
    {
        problem = null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { problem = "unexpected answer from GitHub"; return null; }
        if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
        string tag = Str(root, "tag_name");
        var version = ParseVersion(tag);
        if (version == null) { problem = $"cannot parse release tag '{tag}'"; return null; }
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
        Directory.CreateDirectory(UpdatesDir);
        string final = FileFor(info.Version);
        string partial = final + ".partial";
        IsDownloading = true;
        try
        {
            // The hash first: if it cannot be fetched there is no point transferring 30 MB.
            string expected = await FetchShaAsync(info.ShaUrl, ct).ConfigureAwait(false);
            long done = 0;
            using (var req = new HttpRequestMessage(HttpMethod.Get, info.ExeUrl))
            {
                AddHeaders(req);
                using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
            _settings.PendingUpdateVersion = info.Version.ToString(3);
            _settings.PendingUpdateSha256 = actual;
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
        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
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
    /// would still consider the same file newer than itself). Returns null if fine, otherwise why not.
    /// </summary>
    private static string? CheckEmbeddedVersion(string file, Version version)
    {
        var embedded = EmbeddedVersion(file);
        if (embedded == null) { Log.Warn($"updater: {file} has no readable version resource"); return null; }
        if (embedded.Value.Version != version || embedded.Value.Prerelease != null)
            return $"the exe reports v{Format(embedded.Value)} instead of v{version.ToString(3)}";
        return null;
    }

    /// <summary>
    /// Re-checks a downloaded <c>Clink-&lt;ver&gt;.exe</c> against what was verified when it was downloaded (hash recorded in
    /// settings) and the version it reports, so a file corrupted or swapped in the updates folder since then is never
    /// installed. Returns null if fine, otherwise why not.
    /// </summary>
    private string? VerifyDownload(string file, Version version)
    {
        if (!LooksLikeExe(file)) return "it is not a Windows executable";
        if (!string.Equals(_settings.PendingUpdateVersion, version.ToString(3), StringComparison.Ordinal) || string.IsNullOrEmpty(_settings.PendingUpdateSha256))
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
    public bool TryGetPendingUpdate(out string path, out Version version)
    {
        path = ""; version = CurrentVersion; InstallProblem = null;
        try
        {
            // A build without UpdateRepo never downloaded anything; the updates folder is shared by every Clink exe on the machine.
            if (Repo == null || !Directory.Exists(UpdatesDir)) return false;
            string? best = null; Version? bestVer = null;
            foreach (string file in Directory.EnumerateFiles(UpdatesDir, "Clink-*.exe"))
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var v = VersionFromFile(file);
                if (v == null || !IsNewer(v) || (bestVer != null && v <= bestVer)) continue;
                best = file; bestVer = v;
            }
            if (best == null || bestVer == null) return false;

            if (File.Exists(InstalledMarker(best)))
            {
                Log.Warn($"updater: v{bestVer} was already installed from {best} but this exe is v{VersionDisplay}; not installing it again");
                return false;
            }
            string? reason = FailedReason(best);
            if (reason != null)
            {
                InstallProblem = $"Update to v{bestVer} could not be installed: {reason}. Move Clink.exe to a folder you can write to, or {HowToInstallByHand}.";
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
    public void ReportInstallFailure(Exception ex, Version version, string pendingExe)
    {
        bool appControl = ex.Message.Contains("Application Control", StringComparison.OrdinalIgnoreCase);
        string why = appControl
            ? "Windows App Control (Smart App Control) blocked the downloaded exe because Clink releases are not code-signed yet. Either turn Smart App Control off (Windows Security › App & browser control) or build Clink from source on this PC"
            : Describe(ex);
        InstallProblem = $"Update to v{version} could not start: {why}.";
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
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start the updater process");
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
            Log.Info($"[update] installed v{Format(GetCurrentSemVer())} to {target} (previous exe kept as {backup} until the new one has started)");
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

        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target) ?? "" };
            foreach (string a in restartArgs) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex) { Log.Error("[update] could not restart Clink", ex); }
        return 0;
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
    /// Cleans up after a finished install 30 s after start, then (only with <see cref="Settings.AutoUpdate"/> on) checks GitHub
    /// right away and every 6 hours, downloading a newer release automatically. With auto-update off Clink never contacts GitHub
    /// on its own; the version label in the footer still checks on demand.
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
                CleanupInstalled();   // after the delay: right after an update the apply process may still be exiting
                if (!_settings.AutoUpdate) { Log.Info("updater: AutoUpdate is off, not checking GitHub (click the version in the footer to check by hand)"); return; }
                while (!ct.IsCancellationRequested)
                {
                    await CheckAndMaybeDownloadAsync(ct, manual: false).ConfigureAwait(false);
                    await Task.Delay(CheckInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("updater: loop stopped: " + ex.Message); }
        }, ct);
    }

    /// <summary>Manual check (also downloads when auto-update is on, and retries a download whose install failed). Never throws; null = nothing newer or the check failed (see <see cref="LastError"/>).</summary>
    public async Task<UpdateInfo?> CheckNowAsync()
    {
        try { return await CheckAndMaybeDownloadAsync(_cts.Token, manual: true).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("updater: manual check failed: " + ex.Message); LastError = ex.Message; return null; }
    }

    private async Task<UpdateInfo?> CheckAndMaybeDownloadAsync(CancellationToken ct, bool manual)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await CheckAsync(ct).ConfigureAwait(false);
            if (info == null) return null;
            if (PendingVersion != null && PendingVersion >= info.Version) return info;   // already downloaded in this run
            UpdateAvailable?.Invoke(info);
            if (!_settings.AutoUpdate) return info;

            // Downloaded by an earlier run? Adopt it if it still matches what was verified back then.
            string existing = FileFor(info.Version);
            if (File.Exists(existing))
            {
                string? failed = FailedReason(existing);
                if (failed != null && !manual)
                {
                    Log.Warn($"updater: v{info.Version} is downloaded but installing it failed earlier ({failed}); click the version in the footer to retry");
                    return info;
                }
                if (File.Exists(InstalledMarker(existing)))
                {
                    Log.Warn($"updater: v{info.Version} was already installed from {existing} but this exe is v{VersionDisplay}; not offering it again");
                    return info;
                }
                string? bad = VerifyDownload(existing, info.Version);
                if (bad == null && info.ExeSize > 0 && new FileInfo(existing).Length != info.ExeSize) bad = "its size differs from the release asset";
                if (bad == null)
                {
                    if (failed != null) { Log.Info($"updater: retrying v{info.Version} after an earlier failure ({failed})"); TryDelete(FailedMarker(existing)); }
                    SetPending(existing, info.Version, "already downloaded");
                    return info;
                }
                Log.Warn($"updater: discarding {existing}: {bad}");
                TryDelete(existing);
                TryDelete(FailedMarker(existing));
                TryDelete(InstalledMarker(existing));
            }

            int tries = _attempts.GetValueOrDefault(info.Version);
            if (tries >= MaxDownloadAttempts) return info;
            _attempts[info.Version] = tries + 1;
            try
            {
                string path = await DownloadAsync(info, null, ct).ConfigureAwait(false);
                SetPending(path, info.Version, "downloaded and verified");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"updater: download of v{info.Version} failed ({tries + 1}/{MaxDownloadAttempts}): {ex.Message}"); }
            return info;
        }
        finally { _gate.Release(); }
    }

    private void SetPending(string path, Version version, string how)
    {
        PendingPath = path;
        PendingVersion = version;
        Log.Info($"updater: v{version} {how}: {path}");
        try { UpdateReady?.Invoke(version); } catch (Exception ex) { Log.Warn("updater: UpdateReady handler: " + ex.Message); }
    }

    /// <summary>
    /// Removes the previous exe kept by <see cref="RunApplyUpdate"/> (this process started, so it is no longer needed), downloads
    /// that are not newer than the running exe, stale partials and orphaned markers.
    /// </summary>
    private void CleanupInstalled()
    {
        try
        {
            string? self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self)) { TryDelete(self + ".bak"); TryDelete(self + ".new"); }
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (string file in Directory.EnumerateFiles(UpdatesDir, "Clink-*.exe"))
            {
                if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var v = VersionFromFile(file);
                if (v == null || !IsNewer(v)) { TryDelete(file); TryDelete(FailedMarker(file)); TryDelete(InstalledMarker(file)); }
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
