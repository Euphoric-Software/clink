using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clink.Services;

/// <summary>
/// The temp cache for the smaller copies that Share makes of a clip that is over the Discord or Copium limit
/// (<c>&lt;root&gt;\&lt;key&gt;\&lt;original name&gt;.mp4</c>): the library only ever shows the original, the copy is found again by its
/// key, is never deleted while something uses it, and is cleaned up after a day and above a size cap.
/// </summary>
/// <remarks>
/// <para>The root is <c>%LOCALAPPDATA%\Clink\share-cache</c>, or <c>share-cache</c> inside the data folder of a <c>--dev</c> run
/// (<see cref="Settings.DirOverride"/>), so a dev run never touches the real cache.</para>
/// <para>Contract for callers: in an elevated process (<see cref="ProcessIsElevated"/>) the cache is not used at all: <see cref="IsAvailable"/>
/// is false, <see cref="TryGetReusable"/> finds nothing, <see cref="BeginCopy"/> and <see cref="MarkFinished"/> throw
/// <see cref="ShareCacheUnavailableException"/> and cleanup does nothing, so an administrator run never writes, reuses or deletes there.
/// When not elevated, <see cref="BeginCopy"/> and <see cref="MarkFinished"/> still throw that exception when the root is not where it
/// should be (Windows' final path of it is not the root: a junction above it) or the key folder is a reparse point. Whoever gets the
/// exception shares the original instead; its <see cref="Exception.Message"/> says why, in words for the user.</para>
/// <para>Cleanup only ever deletes inside the root, never follows or deletes through a reparse point (junction, symbolic link), never
/// deletes the root itself, skips what is locked, never touches a name that ends with a dot or a space, and never clears the read-only
/// attribute of a file with more than one hard link. It does not run in an elevated process, on a network drive, or when the root is
/// really somewhere else. It only touches folders whose name is a key (<see cref="IsKey"/>). Every folder it works in is held open
/// without delete sharing while it does: that stops the folder being renamed or deleted (so it cannot be swapped for a junction by
/// renaming it away), but not an in-place conversion of the folder into a reparse point; files and folders are deleted by path, each
/// one looked at again just before. Paths are always used in the <c>\\?\</c> form, so what is checked and what is deleted name the same
/// object.</para>
/// </remarks>
internal sealed class ShareCache : IDisposable
{
    /// <summary>Entries not used for this long are deleted.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);
    /// <summary>The cache is trimmed (oldest first) to at most this many bytes.</summary>
    public const long DefaultMaxBytes = 5L * 1024 * 1024 * 1024;
    /// <summary>An empty folder is only removed once it is this old (a copy is about to be written into a folder just made).</summary>
    public static readonly TimeSpan EmptyFolderGrace = TimeSpan.FromMinutes(10);

    private const string PartSuffix = ".part";
    private const string CopyExtension = ".mp4";
    private const int KeyLength = 32;
    /// <summary>The longest path that works without long-path support (MAX_PATH less the terminating NUL): the part path stays within it.</summary>
    private const int MaxPathChars = 259;
    /// <summary>A name is never cut shorter than this (a root so deep that even this does not fit gets a longer path).</summary>
    private const int MinNameChars = 8;
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);
    /// <summary>The longest a <see cref="MarkInUse"/> waits for a delete of its own key that is in progress (the delete stops at its next file once the mark is there).</summary>
    private static readonly TimeSpan MarkWait = TimeSpan.FromSeconds(5);

    /// <summary>What <see cref="ShareCacheUnavailableException"/> says in an elevated process.</summary>
    public const string ElevatedReason = "not available while Clink runs as administrator";
    private const string RedirectedReason = "not available because the share cache folder is redirected somewhere else (a junction or link)";
    private const string LinkedReason = "not available because the share cache folder for this copy is a junction or link";

    /// <summary>What a <see cref="Cleanup"/> did. <see cref="EntriesSkipped"/>: entries that were due but in use or (partly) locked, and folders that are not entries (links, other names).</summary>
    public readonly record struct CleanupResult(int EntriesDeleted, long BytesFreed, int EntriesSkipped);

    private static readonly object s_currentGate = new();
    private static ShareCache? s_current;

    /// <summary>The cache the app uses (created on first use, after <see cref="Settings.DirOverride"/> is set). Thread-safe.</summary>
    public static ShareCache Current
    {
        get { lock (s_currentGate) return s_current ??= new ShareCache(DefaultRoot()); }
    }

    /// <summary>Disposes <see cref="Current"/> when it was ever created (never creates it); the next <see cref="Current"/> makes a new one.</summary>
    internal static void DisposeCurrent()
    {
        ShareCache? c;
        lock (s_currentGate) { c = s_current; s_current = null; }
        c?.Dispose();
    }

    /// <summary>For the unit tests: forget <see cref="Current"/> (disposing it), so the next use picks up the test's <see cref="Settings.DirOverride"/>.</summary>
    internal static void ResetCurrentForTests() => DisposeCurrent();

    /// <summary>The normal root, or the one inside the <c>--dev</c> data folder.</summary>
    public static string DefaultRoot() =>
        Settings.DirOverride is { Length: > 0 } dev
            ? Path.Combine(dev, "share-cache")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clink", "share-cache");

    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool> _isElevated;
    private readonly TimeSpan _maxAge;
    private readonly long _maxBytes;
    // The in-use marks and the keys being deleted. An entry's last in-use check and its entry into _deleting happen under it; the
    // delete itself does not hold it, so only a mark for the key being deleted waits (Monitor.Wait), and marks for other keys never do.
    private readonly object _gate = new();
    private readonly object _timerGate = new();
    private readonly Dictionary<string, int> _inUse = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleting = new(StringComparer.Ordinal);
    private readonly string _extRoot;   // Root in the \\?\ form: everything cleanup checks and deletes is named from it
    private Timer? _timer;
    private int _cleaning;
    private int _elevatedLogged, _networkLogged, _rootLogged, _oddNameLogged, _multiLinkLogged;
    private volatile bool _disposed;

    public string Root { get; }

    /// <summary>
    /// False in an elevated process: then nothing is reused, <see cref="BeginCopy"/> and <see cref="MarkFinished"/> throw
    /// <see cref="ShareCacheUnavailableException"/> (with <see cref="ElevatedReason"/>) and nothing is cleaned. True does not promise that
    /// <see cref="BeginCopy"/> succeeds (the root or key folder may turn out to be a link).
    /// </summary>
    public bool IsAvailable => !_isElevated();

    /// <summary>For the unit tests: called with the key of each entry cleanup is about to delete, after it was measured and before the last checks.</summary>
    internal Action<string>? BeforeDeleteHook { get; set; }

    /// <summary>For the unit tests: called with the (<c>\\?\</c>) path of each file or folder cleanup is about to delete, just before its last in-use check and the delete.</summary>
    internal Action<string>? BeforeEachDeleteHook { get; set; }

    /// <summary>True while a <see cref="Cleanup"/> runs.</summary>
    internal bool IsCleaning => Volatile.Read(ref _cleaning) == 1;

    /// <summary>True while cleanup is deleting <paramref name="key"/>'s folder.</summary>
    internal bool IsBeingDeleted(string key)
    {
        lock (_gate) return _deleting.Contains(key);
    }

    /// <param name="root">The cache folder (made on first write). Refused when empty, relative or a drive's root.</param>
    /// <param name="utcNow">The clock (default: the real one).</param>
    /// <param name="maxAge">Default 24 h.</param>
    /// <param name="maxBytes">Default 5 GB.</param>
    /// <param name="isElevated">Whether this process is elevated (default: <see cref="ProcessIsElevated"/>); the cache is not used at all when it is.</param>
    public ShareCache(string root, Func<DateTime>? utcNow = null, TimeSpan? maxAge = null, long? maxBytes = null, Func<bool>? isElevated = null)
    {
        Root = ValidateRoot(root);
        _extRoot = AppDataRedirect.ExtendedPath(Root);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _isElevated = isElevated ?? ProcessIsElevated;
        _maxAge = maxAge ?? DefaultMaxAge;
        _maxBytes = maxBytes ?? DefaultMaxBytes;
    }

    /// <summary>The full root path, or an <see cref="ArgumentException"/> for an empty, relative or drive-root path.</summary>
    public static string ValidateRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The share cache root is empty.", nameof(root));
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("The share cache root must be a full path: " + root, nameof(root));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? top = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(top) || string.Equals(Path.TrimEndingDirectorySeparator(top), full, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The share cache root must not be a drive root: " + root, nameof(root));
        return full;
    }

    /// <summary>
    /// Whether this process is elevated, for the cache: see <see cref="IsElevatedFrom"/>. <see cref="Updater.IsElevatedOverride"/> stands in
    /// for all of it in the unit tests, <see cref="TokenElevationOverride"/> for the token's elevation only.
    /// </summary>
    internal static bool ProcessIsElevated()
    {
        if (Updater.IsElevatedOverride is bool forced) return forced;
        var (admin, elevation, integrity) = ReadToken();
        return IsElevatedFrom(admin, elevation, integrity);
    }

    /// <summary>For the unit tests: stands in for the token's TokenElevation value (null = what Windows says).</summary>
    internal static Func<int?>? TokenElevationOverride { get; set; }

    /// <summary>The Mandatory Label RID of a High integrity token (S-1-16-12288).</summary>
    internal const int HighIntegrityRid = 0x3000;

    /// <summary>
    /// Elevated when any of these says so: the token is in the Administrators role, its TokenElevation is not 0, or its integrity level is
    /// High or above. Anything Windows would not say (null) counts as elevated, so the cache is then not used.
    /// </summary>
    internal static bool IsElevatedFrom(bool? inAdministratorRole, int? tokenElevation, int? integrityRid) =>
        inAdministratorRole != false || tokenElevation != 0 || !(integrityRid < HighIntegrityRid);

    /// <summary>What the process token says: in the Administrators role, TokenElevation, integrity RID (each null when it cannot be read).</summary>
    internal static (bool? InAdministratorRole, int? TokenElevation, int? IntegrityRid) ReadToken()
    {
        bool? admin = null;
        int? elevation = null, integrity = null;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            try { admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } catch { }
            var token = id.AccessToken;
            elevation = TokenElevationOverride is { } fake ? fake() : TokenElevationOf(token);
            integrity = IntegrityRidOf(token);
        }
        catch { }
        return (admin, elevation, integrity);
    }

    private const int TokenElevationClass = 20, TokenIntegrityLevelClass = 25;

    private static int? TokenElevationOf(SafeAccessTokenHandle token)
    {
        nint buffer = Marshal.AllocHGlobal(sizeof(int));
        try { return GetTokenInformation(token, TokenElevationClass, buffer, sizeof(int), out _) ? Marshal.ReadInt32(buffer) : null; }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The last sub-authority of the token's mandatory label SID (TOKEN_MANDATORY_LABEL: a SID_AND_ATTRIBUTES, the SID behind it).</summary>
    private static int? IntegrityRidOf(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, TokenIntegrityLevelClass, 0, 0, out int need);
        if (need <= 0 || need > 4096) return null;
        nint buffer = Marshal.AllocHGlobal(need);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, need, out _)) return null;
            string sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;   // S-1-16-<rid>
            if (!sid.StartsWith("S-1-16-", StringComparison.Ordinal)) return null;
            return int.TryParse(sid[(sid.LastIndexOf('-') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int rid) ? rid : null;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, nint info, int length, out int returnLength);

    // ---- keys and paths -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A stable key (32 lowercase hex characters) for one copy: the source (full path, size, last write), the limit it must fit, the output
    /// codec and the plan label or any other variant text the caller wants to tell copies apart by. Any of them changing gives another key.
    /// A last write time of <see cref="DateTimeKind.Unspecified"/> kind is taken as UTC (it is never shifted by the local time zone).
    /// </summary>
    public static string Key(string sourcePath, long sourceSize, DateTime sourceLastWriteUtc, long limitBytes, string codec, string? variant = null)
    {
        string path = Path.GetFullPath(sourcePath).ToUpperInvariant();
        DateTime utc = sourceLastWriteUtc.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(sourceLastWriteUtc, DateTimeKind.Utc) : sourceLastWriteUtc.ToUniversalTime();
        var text = string.Join("\n", "clink-share-cache-v1", path, sourceSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            utc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            limitBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), (codec ?? "").Trim().ToUpperInvariant(), variant ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    /// <summary><see cref="Key(string, long, DateTime, long, string, string?)"/> for a file on disk (its current size and last write).</summary>
    public static string KeyFor(string sourcePath, long limitBytes, string codec, string? variant = null)
    {
        var info = new FileInfo(sourcePath);
        return Key(sourcePath, info.Length, info.LastWriteTimeUtc, limitBytes, codec, variant);
    }

    /// <summary>True for what <see cref="Key"/> makes: exactly 32 characters of <c>0-9a-f</c>. Cleanup only ever touches folders named like that.</summary>
    public static bool IsKey(string? text)
    {
        if (text is not { Length: KeyLength }) return false;
        foreach (char c in text)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        return true;
    }

    /// <summary>
    /// Where the finished copy for <paramref name="key"/> lives: <c>&lt;root&gt;\&lt;key&gt;\&lt;original name&gt;.mp4</c>. The name is made
    /// safe (no invalid characters, no device names such as <c>CON</c>) and cut so the part path stays within 259 characters.
    /// </summary>
    public string PathFor(string key, string originalFileName) => Path.Combine(FolderFor(key), CopyName(originalFileName));

    /// <summary>Where the copy is written while it is made; <see cref="MarkFinished"/> turns it into <see cref="PathFor"/>.</summary>
    public string PartPathFor(string key, string originalFileName) => PathFor(key, originalFileName) + PartSuffix;

    /// <summary>
    /// The finished copy when there is one that can be reused: it exists, is not empty and is not a leftover of an unfinished one. Counts as
    /// a use (keeps it a day longer). The caller must hold a <see cref="MarkInUse"/> mark for the key first, or cleanup may delete it meanwhile.
    /// Always false in an elevated process, when the key folder is a reparse point and when the root is not where it should be.
    /// </summary>
    public bool TryGetReusable(string key, string originalFileName, out string path)
    {
        path = PathFor(key, originalFileName);
        if (_isElevated()) return false;
        try
        {
            using var pin = Pin(FolderFor(key));   // missing, or a junction / link: nothing to reuse
            if (pin == null || !IsGenuineRoot(out _)) return false;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || IsReparse(info.Attributes)) return false;
            if (File.Exists(path + PartSuffix)) return false;
            try { File.SetLastWriteTimeUtc(path, _utcNow()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// Makes the folder for a copy that is about to be written (the part file's), and returns the part path. The caller must hold a
    /// <see cref="MarkInUse"/> mark for the key first (and until the copy is no longer needed), or cleanup may delete the folder meanwhile.
    /// Throws <see cref="ShareCacheUnavailableException"/> in an elevated process, when the root is not where it should be (checked before
    /// and after the folders are made) and when the key folder is a reparse point; an <see cref="IOException"/> when the folder cannot be made.
    /// </summary>
    public string BeginCopy(string key, string originalFileName)
    {
        string part = PartPathFor(key, originalFileName);
        if (_isElevated()) throw new ShareCacheUnavailableException(ElevatedReason);
        if (!IsGenuineRoot(out _)) throw new ShareCacheUnavailableException(RedirectedReason);
        string folder = FolderFor(key);
        Directory.CreateDirectory(folder);
        using (var pin = Pin(folder))   // the folder itself, not what a reparse point names: it must be a plain folder
        {
            if (pin == null) throw new ShareCacheUnavailableException(LinkedReason);
            if (!IsGenuineRoot(out _)) throw new ShareCacheUnavailableException(RedirectedReason);
        }
        return part;
    }

    /// <summary>
    /// The copy is complete: the part file becomes the finished copy (replacing an older one) and counts as used now. False when there is
    /// no part file or it is empty. Throws <see cref="ShareCacheUnavailableException"/> in an elevated process, when the key folder became
    /// a reparse point and when the root is not where it should be.
    /// </summary>
    public bool MarkFinished(string key, string originalFileName)
    {
        string part = PartPathFor(key, originalFileName);
        string final = PathFor(key, originalFileName);
        if (_isElevated()) throw new ShareCacheUnavailableException(ElevatedReason);
        string folder = FolderFor(key);
        if (!Path.Exists(folder)) return false;   // no folder, so no part file
        using var pin = Pin(folder);   // held while the part file is renamed: the folder cannot be renamed or deleted meanwhile
        if (pin == null) throw new ShareCacheUnavailableException(LinkedReason);
        if (!IsGenuineRoot(out _)) throw new ShareCacheUnavailableException(RedirectedReason);
        try
        {
            var info = new FileInfo(part);
            if (!info.Exists || info.Length <= 0) return false;
            File.Move(part, final, overwrite: true);
            try { File.SetLastWriteTimeUtc(final, _utcNow()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn("could not finish " + Rel(final) + ": " + Scrub(ex.Message)); return false; }
    }

    // ---- in use -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An open Share window or an upload uses this key's copy: cleanup leaves it alone until every mark is disposed. Disposing a mark
    /// releases it exactly once (a second dispose does nothing). When cleanup is deleting this key's folder right now, the mark is taken
    /// first (the delete stops before its next file or folder) and then this waits for the delete to end (at most 5 s), so once it
    /// returns the copy either is there or is gone for good. A mark for any other key never waits for a delete.
    /// </summary>
    public IDisposable MarkInUse(string key)
    {
        if (!IsKey(key)) throw new ArgumentException("Not a share cache key: " + key, nameof(key));
        bool timedOut = false;
        lock (_gate)
        {
            _inUse[key] = _inUse.GetValueOrDefault(key) + 1;
            if (_deleting.Contains(key))
            {
                long until = Environment.TickCount64 + (long)MarkWait.TotalMilliseconds;
                while (_deleting.Contains(key))
                {
                    long left = until - Environment.TickCount64;
                    if (left <= 0) { timedOut = true; break; }
                    Monitor.Wait(_gate, (int)left);
                }
            }
        }
        if (timedOut) Warn("a copy was taken into use while its folder was still being cleaned up: " + key);
        return new InUseMark(this, key);
    }

    public bool IsInUse(string key)
    {
        lock (_gate) return _inUse.ContainsKey(key);
    }

    private void Release(string key)
    {
        lock (_gate)
        {
            if (!_inUse.TryGetValue(key, out int n)) return;
            if (n <= 1) _inUse.Remove(key); else _inUse[key] = n - 1;
        }
    }

    private sealed class InUseMark(ShareCache cache, string key) : IDisposable
    {
        private ShareCache? _cache = cache;
        public void Dispose() => Interlocked.Exchange(ref _cache, null)?.Release(key);
    }

    // ---- cleanup ------------------------------------------------------------------------------------------------------------------

    /// <summary>Cleanup with the clock this cache was given.</summary>
    public CleanupResult Cleanup() => Cleanup(_utcNow());

    /// <summary>
    /// Deletes the entries last used more than the max age before <paramref name="nowUtc"/>, then, above the size cap, the oldest ones that
    /// are not in use until it fits. Skips in-use entries and locked files, and removes empty folders. Never leaves the root, never follows
    /// a reparse point, does nothing in an elevated process, on a network drive or when the root is really elsewhere. One cleanup at a
    /// time (a second call returns at once).
    /// </summary>
    public CleanupResult Cleanup(DateTime nowUtc)
    {
        if (Interlocked.Exchange(ref _cleaning, 1) == 1) return default;
        try { return CleanupCore(nowUtc.ToUniversalTime()); }
        catch (Exception ex) { Warn("cleanup failed: " + Scrub(ex.Message)); return default; }
        finally { Volatile.Write(ref _cleaning, 0); }
    }

    private sealed class Entry
    {
        public string Dir = "";
        public string Key = "";
        public long Bytes;
        public DateTime Newest;   // the newest write time of anything inside (or of the folder when it is empty)
        public bool Empty;
        public bool InUse;
        public bool Gone;
        public bool Skipped;      // was due, but in use or (partly) locked
    }

    private CleanupResult CleanupCore(DateTime now)
    {
        if (_disposed) return default;
        if (_isElevated())
        {
            WarnOnce(ref _elevatedLogged, "Clink runs as administrator, so old share copies are not cleaned up in this run");
            return default;
        }
        if (IsOnNetwork())
        {
            WarnOnce(ref _networkLogged, "the cache is on a network drive, so old share copies are not cleaned up");
            return default;
        }
        var rootInfo = new DirectoryInfo(_extRoot);
        if (!rootInfo.Exists) return default;
        if (IsReparse(rootInfo.Attributes)) { Warn("the root is a reparse point, not cleaning"); return default; }
        // Held open (no delete sharing) for the whole cleanup: the root cannot be renamed or deleted meanwhile.
        using var rootPin = Pin(_extRoot);
        if (rootPin == null) { Warn("could not open the root as a plain folder, not cleaning"); return default; }
        if (!IsGenuineRoot(out string? final)) { Warn($"the root is really {final ?? "(unknown)"}, not cleaning"); return default; }

        int deleted = 0, otherFolders = 0;
        long freed = 0;
        bool stop = false;
        var entries = new List<Entry>();
        foreach (var dir in SafeDirectories(rootInfo))
        {
            // a junction or link is not ours and never followed; a folder not named like a key is not ours either
            if (IsReparse(dir.Attributes) || !IsKey(dir.Name)) { otherFolders++; continue; }
            var e = new Entry { Dir = dir.FullName, Key = dir.Name, InUse = IsInUse(dir.Name), Newest = dir.LastWriteTimeUtc };
            Measure(dir, e);
            entries.Add(e);
        }

        bool IsOld(Entry e) => now - e.Newest > (e.Empty ? EmptyFolderGrace : _maxAge);

        // A mark taken after the entry was measured is seen: by the last in-use check (under the gate, together with the entry going
        // into _deleting), and by the check before each file and folder delete, after which the delete stops. A mark for this key taken
        // while it is deleted waits for the delete to end; marks for other keys do not wait. The root is checked again too (a junction
        // above it, made meanwhile).
        void Delete(Entry e, bool recheckAge)
        {
            BeforeDeleteHook?.Invoke(e.Key);
            if (_disposed) { stop = true; return; }
            if (IsInUse(e.Key)) { e.InUse = true; e.Skipped = true; return; }
            if (recheckAge)
            {
                Remeasure(e);
                if (!IsOld(e)) return;   // used again since it was measured
            }
            if (!IsGenuineRoot(out string? where)) { Warn($"the root became {where ?? "(unknown)"}, stopping"); stop = true; return; }
            lock (_gate)
            {
                if (_inUse.ContainsKey(e.Key)) { e.InUse = true; e.Skipped = true; return; }
                _deleting.Add(e.Key);
            }
            long before = e.Bytes;
            bool gone;
            try { gone = DeleteEntry(e); }
            finally
            {
                lock (_gate) { _deleting.Remove(e.Key); Monitor.PulseAll(_gate); }
            }
            freed += Math.Max(0, before - e.Bytes);
            if (gone) deleted++; else e.Skipped = true;
        }

        // 1. too old (a copy that is still being made or shown is in use, and an empty folder is only a leftover after a grace time)
        foreach (var e in entries)
        {
            if (stop) break;
            if (!IsOld(e)) continue;
            if (e.InUse) { e.Skipped = true; continue; }
            Delete(e, recheckAge: true);
        }

        // 2. over the cap: oldest first, not in use
        long total = entries.Where(e => !e.Gone).Sum(e => e.Bytes);
        if (!stop && total > _maxBytes)
        {
            foreach (var e in entries.Where(e => !e.Gone && !e.Skipped).OrderBy(e => e.Newest).ThenBy(e => e.Key, StringComparer.Ordinal).ToList())
            {
                if (stop || total <= _maxBytes) break;
                if (e.InUse) { e.Skipped = true; continue; }
                long before = e.Bytes;
                Delete(e, recheckAge: false);
                total -= before - e.Bytes;   // what is really gone (a locked file stays)
            }
        }
        if (deleted > 0 || freed > 0) Info($"removed {deleted} copy folder(s), {freed / (1024 * 1024)} MB");
        return new CleanupResult(deleted, freed, otherFolders + entries.Count(e => e.Skipped));
    }

    /// <summary>
    /// The root's final path (junctions above it followed) is the root itself, in its long form (8.3 names such as <c>JASON~1</c>
    /// expanded, which follows no junction), as Windows' final path always is.
    /// </summary>
    private bool IsGenuineRoot(out string? final)
    {
        final = AppDataRedirect.ResolvedPath(Root);   // opens it with FILE_FLAG_BACKUP_SEMANTICS, GetFinalPathNameByHandle, no \\?\ prefix
        return final != null && string.Equals(Path.TrimEndingDirectorySeparator(final), LongForm(Root), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="path"/> with the 8.3 names in the part of it that exists expanded (GetLongPathNameW), the rest as it is; the path
    /// itself when Windows will not say.
    /// </summary>
    private static string LongForm(string path)
    {
        try
        {
            string existing = path;
            var missing = new Stack<string>();
            while (!Path.Exists(existing))
            {
                string? parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent)) return path;
                missing.Push(Path.GetFileName(existing));
                existing = parent;
            }
            string ext = AppDataRedirect.ExtendedPath(existing);
            var buffer = new char[512];
            uint n = GetLongPathName(ext, buffer, (uint)buffer.Length);
            if (n > buffer.Length)
            {
                buffer = new char[n];
                n = GetLongPathName(ext, buffer, (uint)buffer.Length);
            }
            if (n == 0 || n > buffer.Length) return path;
            string full = new(buffer, 0, (int)n);
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full[8..];
            else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
            foreach (string part in missing) full = Path.Combine(full, part);
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception) { return path; }
    }

    /// <summary>A network drive or a UNC path: never cleaned (what is there and who else uses it cannot be told from here).</summary>
    private bool IsOnNetwork()
    {
        if (Root.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try { return new DriveInfo(Path.GetPathRoot(Root)!).DriveType == DriveType.Network; }
        catch (Exception) { return true; }
    }

    /// <summary>A name that ends with a dot or a space: Windows' normal path form drops that, so such a file or folder is never touched.</summary>
    private static bool IsOddName(string name) => name.Length == 0 || name[^1] is '.' or ' ';

    /// <summary>Sizes and newest write time of the files in an entry; sub-folders are walked only when they are not reparse points, links are ignored.</summary>
    private static void Measure(DirectoryInfo dir, Entry e)
    {
        bool any = false;
        void Walk(DirectoryInfo d)
        {
            foreach (var f in SafeFiles(d))
            {
                if (IsOddName(f.Name)) { any = true; continue; }   // never touched, but the folder is not empty
                if (IsReparse(f.Attributes)) continue;
                any = true;
                try { e.Bytes += f.Length; } catch (IOException) { }
                if (f.LastWriteTimeUtc > e.Newest) e.Newest = f.LastWriteTimeUtc;
            }
            foreach (var sub in SafeDirectories(d))
            {
                if (IsOddName(sub.Name)) { any = true; continue; }
                if (IsReparse(sub.Attributes)) continue;
                any = true;
                Walk(sub);
            }
        }
        // the folder's own time only counts for an empty one: files decide otherwise
        var folderTime = e.Newest;
        e.Newest = DateTime.MinValue;
        Walk(dir);
        e.Empty = !any;
        if (e.Empty) e.Newest = folderTime;
    }

    /// <summary>Measures an entry again from scratch (what is there now); a folder that is gone or became a link measures as empty.</summary>
    private static void Remeasure(Entry e)
    {
        var dir = new DirectoryInfo(e.Dir);
        e.Bytes = 0;
        e.Empty = true;
        e.Newest = DateTime.MinValue;
        try
        {
            if (!dir.Exists) return;
            e.Newest = dir.LastWriteTimeUtc;
            if (!IsReparse(dir.Attributes)) Measure(dir, e);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Deletes the entry's files, then its folders. A locked file or folder stays (logged) and the entry is not gone; a folder that is (or
    /// became) a reparse point is left as it is. Returns whether it is gone; when it is not, <see cref="Entry.Bytes"/> is what is left.
    /// </summary>
    private bool DeleteEntry(Entry e)
    {
        if (!IsInside(e.Dir)) { Warn("refusing to delete outside the root: " + e.Dir); return false; }
        var dir = new DirectoryInfo(e.Dir);
        try
        {
            dir.Refresh();
            if (!dir.Exists) { e.Gone = true; e.Bytes = 0; return true; }
            if (IsReparse(dir.Attributes)) { Warn(Rel(e.Dir) + " became a link, left alone"); return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        bool clean = true;
        using (var pin = Pin(e.Dir))   // held while its contents go: it cannot be renamed or deleted meanwhile
        {
            if (pin == null) clean = false;
            else Purge(dir, e.Key, ref clean);
        }
        if (clean) RemoveEmpty(dir, e.Key, ref clean);
        if (clean) { e.Gone = true; e.Bytes = 0; }
        else Remeasure(e);
        return clean;
    }

    /// <summary>
    /// Deletes the files and folders inside <paramref name="d"/>, which the caller holds pinned. Each one is looked at again right before,
    /// and the delete stops as soon as <paramref name="key"/> is in use.
    /// </summary>
    private void Purge(DirectoryInfo d, string key, ref bool clean)
    {
        foreach (var f in SafeFiles(d))
        {
            if (!DeleteFile(f, key, ref clean) && IsInUse(key)) return;
        }
        foreach (var sub in SafeDirectories(d))
        {
            if (IsOddName(sub.Name)) { clean = false; WarnOnce(ref _oddNameLogged, "left alone a name that ends with a dot or a space: " + Rel(sub.FullName)); continue; }
            if (IsInUse(key)) { clean = false; return; }
            try
            {
                sub.Refresh();
                if (!sub.Exists) continue;
                if (IsReparse(sub.Attributes) || !IsInside(sub.FullName)) { clean = false; continue; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { clean = false; continue; }
            using (var pin = Pin(sub.FullName))
            {
                if (pin == null) { clean = false; continue; }   // a link after all, or it cannot be opened: left alone
                Purge(sub, key, ref clean);
            }
            RemoveEmpty(sub, key, ref clean);
        }
    }

    /// <summary>
    /// Deletes one file (by its <c>\\?\</c> path) unless it is a link, has an odd name, is a read-only file with more than one hard link
    /// (clearing the attribute would clear it for every link, outside the cache too) or <paramref name="key"/> came into use. The read-only
    /// attribute is cleared through a handle to the very file whose link count was read. False when it was not deleted.
    /// </summary>
    private bool DeleteFile(FileInfo f, string key, ref bool clean)
    {
        if (IsOddName(f.Name)) { clean = false; WarnOnce(ref _oddNameLogged, "left alone a name that ends with a dot or a space: " + Rel(f.FullName)); return false; }
        try
        {
            f.Refresh();
            if (!f.Exists) return true;
            if (IsReparse(f.Attributes) || !IsInside(f.FullName)) { clean = false; return false; }   // never delete a link or what it points to: leave it
            if ((f.Attributes & FileAttributes.ReadOnly) != 0)
            {
                using var h = OpenItself(f.FullName, directory: false, FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_ALL, out var info);
                if (h == null) { clean = false; Warn("skipped " + Rel(f.FullName) + ": it could not be opened to clear its read-only attribute"); return false; }
                if ((info.Attributes & FILE_ATTRIBUTE_READONLY) != 0)
                {
                    if (info.Links > 1)
                    {
                        clean = false;
                        WarnOnce(ref _multiLinkLogged, "left alone a read-only file with more than one hard link: " + Rel(f.FullName));
                        return false;
                    }
                    if (!ClearReadOnly(h, info.Attributes)) { clean = false; Warn("skipped " + Rel(f.FullName) + ": its read-only attribute could not be cleared"); return false; }
                }
            }
            BeforeEachDeleteHook?.Invoke(f.FullName);
            if (IsInUse(key)) { clean = false; return false; }
            f.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            clean = false;
            Warn("skipped " + Rel(f.FullName) + ": " + Scrub(ex.Message));
            return false;
        }
    }

    private bool RemoveEmpty(DirectoryInfo d, string key, ref bool clean)
    {
        try
        {
            d.Refresh();
            if (!d.Exists) return true;
            if (!IsInside(d.FullName) || IsReparse(d.Attributes)) { clean = false; return false; }
            if (d.EnumerateFileSystemInfos().Any()) { clean = false; return false; }
            if ((d.Attributes & FileAttributes.ReadOnly) != 0)
            {
                using var h = OpenItself(d.FullName, directory: true, FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_ALL, out var info);
                if (h == null || ((info.Attributes & FILE_ATTRIBUTE_READONLY) != 0 && !ClearReadOnly(h, info.Attributes)))
                {
                    clean = false;
                    Warn("skipped folder " + Rel(d.FullName) + ": its read-only attribute could not be cleared");
                    return false;
                }
            }
            BeforeEachDeleteHook?.Invoke(d.FullName);
            if (IsInUse(key)) { clean = false; return false; }   // BeginCopy may be about to write into it
            d.Delete(recursive: false);   // empty only (and RemoveDirectory on a junction swapped in now removes the link, never its target)
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { clean = false; Warn("skipped folder " + Rel(d.FullName) + ": " + Scrub(ex.Message)); return false; }
    }

    /// <summary>A path below the root in the <c>\\?\</c> form, without empty, <c>.</c> or <c>..</c> parts (that form is taken literally).</summary>
    private bool IsInside(string path)
    {
        string prefix = _extRoot + Path.DirectorySeparatorChar;
        if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string part in path[prefix.Length..].Split(Path.DirectorySeparatorChar))
            if (part is "" or "." or "..") return false;
        return true;
    }

    // ---- logging: paths relative to the root, which is logged once ---------------------------------------------------------------

    /// <summary><paramref name="path"/> relative to the root when it is inside it (either form), otherwise as it is.</summary>
    private string Rel(string path)
    {
        foreach (string root in new[] { _extRoot, Root })
            if (path.Length > root.Length + 1 && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return path[(root.Length + 1)..];
        return path;
    }

    /// <summary>A Windows message with the root in it (as a .NET IOException names the file) shortened to <c>&lt;cache&gt;</c>.</summary>
    private string Scrub(string message) =>
        message.Replace(_extRoot, "<cache>", StringComparison.OrdinalIgnoreCase).Replace(Root, "<cache>", StringComparison.OrdinalIgnoreCase);

    private void NoteRoot()
    {
        if (Interlocked.Exchange(ref _rootLogged, 1) == 0) Log.Info("share cache: the cache is " + Root + " (paths in its messages are inside it)");
    }

    private void Warn(string what) { NoteRoot(); Log.Warn("share cache: " + what); }

    private void Info(string what) { NoteRoot(); Log.Info("share cache: " + what); }

    private void WarnOnce(ref int logged, string what)
    {
        if (Interlocked.Exchange(ref logged, 1) == 0) Warn(what);
    }

    private static bool IsReparse(FileAttributes a) => (a & FileAttributes.ReparsePoint) != 0;

    private static IEnumerable<DirectoryInfo> SafeDirectories(DirectoryInfo d)
    {
        try { return d.EnumerateDirectories().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<DirectoryInfo>(); }
    }

    private static IEnumerable<FileInfo> SafeFiles(DirectoryInfo d)
    {
        try { return d.EnumerateFiles().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<FileInfo>(); }
    }

    /// <summary>
    /// Opens <paramref name="dir"/> itself (a reparse point is not followed) for listing, sharing read and write but not delete, so while
    /// the handle is open the folder cannot be renamed or deleted (and so not swapped for a junction by renaming it away). It does not stop
    /// the folder itself being turned into a reparse point in place, which is why every child is looked at again before it is deleted.
    /// Null when it is not a plain folder (a reparse point, a file) or cannot be opened.
    /// </summary>
    private static SafeFileHandle? Pin(string dir) => OpenItself(dir, directory: true, FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE, out _);

    /// <summary>
    /// Opens <paramref name="path"/> itself, never what a reparse point names, in the <c>\\?\</c> form, and reads what it is through the
    /// handle. Null when it is a reparse point, is not a folder (<paramref name="directory"/>) or a file (otherwise), or cannot be opened.
    /// </summary>
    private static SafeFileHandle? OpenItself(string path, bool directory, uint access, uint share, out ByHandleFileInformation info)
    {
        info = default;
        SafeFileHandle h;
        try
        {
            h = CreateFile(AppDataRedirect.ExtendedPath(path), access, share, 0, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, 0);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (h.IsInvalid) { h.Dispose(); return null; }
        uint want = directory ? FILE_ATTRIBUTE_DIRECTORY : 0;
        if (!GetFileInformationByHandle(h, out info) || (info.Attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT)) != want)
        {
            h.Dispose();
            return null;
        }
        return h;
    }

    /// <summary>Clears the read-only attribute of the file or folder <paramref name="h"/> is open on (needs FILE_WRITE_ATTRIBUTES), keeping the other settable ones.</summary>
    private static bool ClearReadOnly(SafeFileHandle h, uint attributes)
    {
        uint keep = attributes & SettableAttributes & ~FILE_ATTRIBUTE_READONLY;
        var basic = new FileBasicInfo { FileAttributes = keep == 0 ? FILE_ATTRIBUTE_NORMAL : keep };   // times 0: unchanged
        return SetFileInformationByHandle(h, FileBasicInfoClass, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>());
    }

    private const uint FILE_LIST_DIRECTORY = 0x1, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4, FILE_SHARE_ALL = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint SECURITY_SQOS_PRESENT = 0x00100000, SECURITY_IDENTIFICATION = 0x00010000;
    private const uint FILE_ATTRIBUTE_READONLY = 0x1, FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    /// <summary>Read-only, hidden, system, archive, temporary, offline, not content indexed: what FileBasicInfo may set.</summary>
    private const uint SettableAttributes = 0x1 | 0x2 | 0x4 | 0x20 | 0x100 | 0x1000 | 0x2000;
    private const int FileBasicInfoClass = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
        public uint Padding;   // FILE_BASIC_INFO is 40 bytes (8-byte alignment)
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileBasicInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, [Out] char[] longPath, uint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    // ---- schedule -----------------------------------------------------------------------------------------------------------------

    /// <summary>Runs <see cref="Cleanup()"/> after <paramref name="firstDelay"/> and then every <paramref name="period"/> on a pool thread (default: 15 s, then hourly).</summary>
    public void StartSchedule(TimeSpan? firstDelay = null, TimeSpan? period = null)
    {
        lock (_timerGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ShareCache));
            _timer?.Dispose();
            _timer = new Timer(_ => Cleanup(), null, firstDelay ?? TimeSpan.FromSeconds(15), period ?? TimeSpan.FromHours(1));
        }
    }

    /// <summary>Stops the schedule; a cleanup that is running stops after the entry it is on, and is waited for up to 2 s.</summary>
    public void Dispose()
    {
        Timer? timer;
        lock (_timerGate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        if (timer == null) return;
        var done = new ManualResetEvent(false);
        // The event is only disposed once signalled: a timer callback still running after the wait signals it later.
        if (timer.Dispose(done) && done.WaitOne(DisposeWait)) done.Dispose();
    }

    // ---- names --------------------------------------------------------------------------------------------------------------------

    private static readonly HashSet<string> s_deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    /// <summary>The copy's file name: the original's name without invalid characters or a device name, cut to fit, with <c>.mp4</c>.</summary>
    private string CopyName(string? originalFileName)
    {
        string name = Path.GetFileNameWithoutExtension(originalFileName ?? "");
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // <root>\<key>\<name>.mp4.part within MaxPathChars
        int max = Math.Max(MinNameChars, MaxPathChars - (Root.Length + 1 + KeyLength + 1) - CopyExtension.Length - PartSuffix.Length);
        string fitted = NotADevice(Fit(name, max));
        if (fitted.Length > max) fitted = NotADevice(Fit(name, max - 1));   // the "_" a cut device name gets still fits
        return fitted + CopyExtension;
    }

    /// <summary>Trimmed, at most <paramref name="max"/> characters (never half a surrogate pair), no trailing dot or space; "clip" when nothing is left.</summary>
    private static string Fit(string name, int max)
    {
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length > max)
        {
            int cut = max;
            if (char.IsHighSurrogate(name[cut - 1])) cut--;
            name = name[..cut].TrimEnd(' ', '.');
        }
        return name.Length == 0 ? "clip" : name;
    }

    /// <summary>"CON" -> "CON_", "nul.x" -> "nul_.x": Windows takes a name whose part before the first dot is a device name for the device.</summary>
    private static string NotADevice(string name)
    {
        int dot = name.IndexOf('.');
        string first = dot < 0 ? name : name[..dot];
        return s_deviceNames.Contains(first.TrimEnd(' ')) ? first + "_" + name[first.Length..] : name;
    }

    private string FolderFor(string key)
    {
        if (!IsKey(key)) throw new ArgumentException("Not a share cache key: " + key, nameof(key));
        return Path.Combine(Root, key);
    }
}

/// <summary>
/// The share cache cannot be used for this copy (<see cref="ShareCache.BeginCopy"/>, <see cref="ShareCache.MarkFinished"/>): Clink runs as
/// administrator (<see cref="ShareCache.ElevatedReason"/>), or the cache folder is redirected or a link. <see cref="Exception.Message"/>
/// says why, in words for the user; the caller shares the original instead. An <see cref="IOException"/>, so a handler of file errors
/// around the copy sees it too.
/// </summary>
internal sealed class ShareCacheUnavailableException(string reason) : IOException(reason);
