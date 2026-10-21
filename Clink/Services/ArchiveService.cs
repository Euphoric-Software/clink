using System.Diagnostics;
using System.IO;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Clink.Services;

/// <summary>
/// Moves clips from the local clips folder to the archive location (NAS share, mapped drive, second drive) in the background:
/// docs/SPEC-v2.md §9. Runs 1 min after a clip is saved and every 5 min; picks eligible clips oldest-first (older than
/// <see cref="Settings.ArchiveAfterDays"/>, or while the local folder is above <see cref="Settings.ArchiveWhenLocalExceedsGB"/>);
/// copies on a lowest-priority thread in small chunks with sleeps in between, verifies the length, deletes the local file and
/// points the <see cref="ClipEntry"/> at the archive. Never touches a file that is open, never deletes a local file whose copy
/// was not verified, and quietly retries later when the archive is unreachable.
/// </summary>
internal sealed class ArchiveService
{
    private const int ChunkBytes = 512 * 1024;
    private const int ChunkSleepMs = 8;           // ~60 MB/s at most, so a NAS copy never competes with the game
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AfterClip = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstRun = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MinAge = TimeSpan.FromSeconds(30); // a file written seconds ago may still be in use by the muxer / uploader

    private readonly Settings _settings;
    private readonly ClipLibrary _library;
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly byte[] _buffer = new byte[ChunkBytes];
    private Thread? _thread;
    private volatile bool _stop;
    private DateTime _due = DateTime.MaxValue;      // UTC
    private TaskCompletionSource? _runNow;
    private string _status = "Archive off";
    private DateTime? _lastMove;
    private bool _reachable;
    private string? _problem;

    public ArchiveService(Settings settings, ClipLibrary library)
    {
        _settings = settings;
        _library = library;
    }

    /// <summary>One line for the UI ("12 clips archived · last move 14:02 · NAS reachable", "archive unreachable, will retry", "Copying X (40 %)").</summary>
    public string Status { get { lock (_gate) return _status; } }

    /// <summary>Raised (on the archive thread) whenever <see cref="Status"/> changes or a clip was moved. Marshal to the UI.</summary>
    public event Action? Changed;

    /// <summary>True while a run is in progress.</summary>
    public bool IsBusy { get; private set; }

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _due = DateTime.UtcNow + FirstRun;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Clink.Archive", Priority = ThreadPriority.Lowest };
        _thread.Start();
        SetStatus(IdleStatus());
    }

    public void Stop()
    {
        _stop = true;
        _wake.Set();
        var t = _thread;
        _thread = null;
        try { t?.Join(5000); } catch { }
    }

    /// <summary>A clip was just saved: run in a minute (sooner if a run is already due).</summary>
    public void NotifyClipSaved() => Schedule(DateTime.UtcNow + AfterClip);

    /// <summary>Runs now (the "Archive now" button); completes when the run has finished.</summary>
    public Task RunNowAsync()
    {
        if (_thread == null) Start();
        TaskCompletionSource tcs;
        lock (_gate)
        {
            _runNow ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _runNow;
        }
        Schedule(DateTime.UtcNow);
        return tcs.Task;
    }

    private void Schedule(DateTime dueUtc)
    {
        lock (_gate) if (dueUtc < _due) _due = dueUtc;
        _wake.Set();
    }

    /// <summary>
    /// The archive settings changed in the UI: the status line is recomputed right away (off / no rule / N archived) and a
    /// run is scheduled shortly, which fills in reachability and starts moving what is eligible. Any thread.
    /// </summary>
    public void Reevaluate()
    {
        var s = _settings;
        string? root = s.ArchivePath?.Trim();
        if (!s.ArchiveEnabled || string.IsNullOrEmpty(root)) { _problem = null; _reachable = false; SetStatus("Archive off"); return; }
        if (s.ArchiveAfterDays <= 0 && s.ArchiveWhenLocalExceedsGB <= 0) { _problem = null; _reachable = false; SetStatus("Archive on, but no rule set (days / GB)"); return; }
        if (IsSameFolder(root, s.ClipsFolder)) { _problem = SameFolderProblem; _reachable = false; SetStatus(IdleStatus()); return; }
        _problem = null; _reachable = false;   // a new location: nothing is known about it until the run looks
        SetStatus(IdleStatus());
        Schedule(DateTime.UtcNow + TimeSpan.FromSeconds(15));
    }

    private void Loop()
    {
        while (!_stop)
        {
            DateTime due;
            lock (_gate) due = _due;
            TimeSpan wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) { _wake.WaitOne(wait > Period ? Period : wait); continue; }
            if (_stop) break;
            TaskCompletionSource? tcs;
            lock (_gate) { tcs = _runNow; _runNow = null; _due = DateTime.UtcNow + Period; }
            IsBusy = true;
            try { Run(); }
            catch (Exception ex) { Log.Warn("archive: " + ex.Message); SetStatus(Describe(ex) + ", will retry"); }
            finally { IsBusy = false; }
            tcs?.TrySetResult();
        }
        lock (_gate) { _runNow?.TrySetResult(); _runNow = null; }
    }

    // ------------------------------------------------------------------ one run

    private void Run()
    {
        var s = _settings;
        string? root = s.ArchivePath?.Trim();
        if (!s.ArchiveEnabled || string.IsNullOrEmpty(root)) { _problem = null; SetStatus("Archive off"); return; }
        if (s.ArchiveAfterDays <= 0 && s.ArchiveWhenLocalExceedsGB <= 0) { _problem = null; _reachable = false; SetStatus("Archive on, but no rule set (days / GB)"); return; }
        if (IsSameFolder(root, s.ClipsFolder))
        {
            // "Move" onto itself = delete: the UI refuses this spelling, but settings.json can be edited by hand.
            _problem = SameFolderProblem; _reachable = false;
            Log.Warn("archive: the archive location is the clips folder, nothing is moved");
            SetStatus(IdleStatus());
            return;
        }

        string? problem = Reach(root);
        if (problem != null)
        {
            _reachable = false; _problem = problem;
            Log.Info("archive: " + problem + ", will retry");
            SetStatus(IdleStatus());
            return;
        }
        _reachable = true; _problem = null;

        string local = s.ClipsFolder;
        long limitBytes = s.ArchiveWhenLocalExceedsGB > 0 ? (long)(s.ArchiveWhenLocalExceedsGB * (1L << 30)) : 0;
        long localBytes = limitBytes > 0 ? FolderBytes(local) : 0;
        DateTime cutoff = s.ArchiveAfterDays > 0 ? DateTime.Now.AddDays(-s.ArchiveAfterDays) : DateTime.MinValue;
        var candidates = _library.Entries
            .Where(e => !e.IsArchived && !string.IsNullOrEmpty(e.Path) && IsUnder(e.Path, local))
            .OrderBy(e => e.Created)
            .ToList();

        int moved = 0;
        foreach (var e in candidates)
        {
            if (_stop) break;
            bool byAge = s.ArchiveAfterDays > 0 && e.Created < cutoff;
            bool bySize = limitBytes > 0 && localBytes > limitBytes;
            if (!byAge && !bySize) continue;
            FileInfo fi;
            try { fi = new FileInfo(e.Path); if (!fi.Exists) continue; } catch { continue; }
            if (DateTime.UtcNow - fi.LastWriteTimeUtc < MinAge) continue;

            var outcome = MoveOne(e, fi, root);
            if (outcome == Outcome.Unreachable) { _reachable = false; break; }
            if (outcome == Outcome.Moved) { moved++; localBytes -= fi.Length; }
        }
        if (moved > 0) Log.Info($"archive: run finished, {moved} clip(s) moved to {root}");
        SetStatus(IdleStatus());
    }

    private enum Outcome { Moved, Skipped, Unreachable }

    private Outcome MoveOne(ClipEntry entry, FileInfo fi, string root)
    {
        string src = fi.FullName;
        string name = fi.Name;
        long length = fi.Length;
        DateTime lastWrite = fi.LastWriteTimeUtc;

        // Open? (uploading / trimming / playing) Then not now.
        try { using var probe = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException) { Log.Info($"archive: {name} is in use, later"); return Outcome.Skipped; }
        catch (UnauthorizedAccessException) { Log.Info($"archive: {name} is in use, later"); return Outcome.Skipped; }

        string dest = Path.Combine(root, name);
        var sw = Stopwatch.StartNew();
        try
        {
            bool haveCopy = false;
            if (File.Exists(dest))
            {
                // The same file under another spelling (mapped drive, \\PC\share, junction, case) must never reach the delete below.
                bool? same = SameFile(src, dest);
                if (same != false)
                {
                    _problem = same == null ? "cannot tell the archive copy from the local file" : SameFolderProblem;
                    Log.Warn($"archive: {name}: {(same == null ? "could not compare" : "the archive path is")} the local file itself, nothing moved");
                    return Outcome.Skipped;
                }
                // Left over from a run whose local delete failed: our copy when the length and both ends match; anything else gets a new name.
                if (LooksLikeCopy(src, dest, length)) haveCopy = true;
                else dest = UniqueName(root, name);
            }
            if (!haveCopy)
            {
                string tmp = dest + ".clinkpart";
                try
                {
                    Copy(src, tmp, name, length);
                    long copied = new FileInfo(tmp).Length;
                    var after = new FileInfo(src);
                    if (copied != length || after.Length != length || after.LastWriteTimeUtc != lastWrite)
                    {
                        Log.Warn($"archive: {name} changed while copying or the copy is short ({copied} of {length} bytes), local file kept");
                        TryDelete(tmp);
                        return Outcome.Skipped;
                    }
                    File.Move(tmp, dest, true);
                }
                catch
                {
                    TryDelete(tmp);
                    throw;
                }
                try { File.SetCreationTimeUtc(dest, fi.CreationTimeUtc); File.SetLastWriteTimeUtc(dest, lastWrite); } catch { }
            }

            // Verified copy exists: now (and only now) the local file goes. The copy took a while (throttled), so the file is
            // checked once more under an exclusive-ish handle right before the delete: the trim editor may have opened it or
            // renamed its cut over it meanwhile, and that version is not what the archive holds.
            try
            {
                using var hold = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Delete, 1);
                if (hold.Length != length || File.GetLastWriteTimeUtc(src) != lastWrite)
                {
                    Log.Warn($"archive: {name} changed after it was copied, local file kept");
                    if (!haveCopy) TryDelete(dest);   // the stale copy we just made
                    return Outcome.Skipped;
                }
                File.Delete(src);   // marked for deletion now, gone when the handle closes; a rename over it fails until then
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"archive: copied {name} but the local file is in use or could not be deleted yet ({ex.Message}); next run finishes the move");
                return Outcome.Skipped;
            }

            entry.Path = dest;
            entry.ArchivedAt = DateTime.Now;
            foreach (var other in _library.Entries)
                if (other != entry && string.Equals(other.SourcePath, src, StringComparison.OrdinalIgnoreCase)) other.SourcePath = dest;
            _library.Update(entry);
            _lastMove = DateTime.Now;
            Log.Info($"archive: moved {src} -> {dest} ({length / 1_048_576.0:0.0} MB in {sw.Elapsed.TotalSeconds:0.0} s)");
            return Outcome.Moved;
        }
        catch (OperationCanceledException) { return Outcome.Skipped; }
        catch (Exception ex)
        {
            string why = Describe(ex);
            _problem = why;
            Log.Warn($"archive: {name}: {ex.Message} ({why}); local file kept");
            return IsUnreachable(ex) ? Outcome.Unreachable : Outcome.Skipped;
        }
    }

    /// <summary>Chunked copy with a sleep between chunks; a progress line goes to <see cref="Status"/> every 5 %.</summary>
    private void Copy(string src, string dst, string name, long length)
    {
        using var inp = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var outp = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
        long done = 0;
        int lastStep = -1, n;
        SetStatus($"Copying {name}…");
        while ((n = inp.Read(_buffer, 0, _buffer.Length)) > 0)
        {
            if (_stop) throw new OperationCanceledException();
            outp.Write(_buffer, 0, n);
            done += n;
            int step = (int)(done * 20 / Math.Max(1, length));
            if (step != lastStep) { lastStep = step; SetStatus($"Copying {name} ({step * 5} %)"); }
            Thread.Sleep(ChunkSleepMs);
        }
        outp.Flush(true);
    }

    // ------------------------------------------------------------------ test / status

    /// <summary>The "Test" button: reachable? writable? free space. Never throws.</summary>
    public Task<(bool ok, string message, long freeBytes)> TestAsync(string path) => Task.Run(() =>
    {
        string p = (path ?? "").Trim();
        if (p.Length == 0) return (false, "Enter a folder or share path first", 0L);
        if (IsSameFolder(p, _settings.ClipsFolder)) return (false, "Archive location is the clips folder: pick another folder, share or drive", 0L);
        string? problem = Reach(p);
        if (problem != null) return (false, Capitalize(problem), 0L);
        try
        {
            string probe = Path.Combine(p, $".clink-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, new byte[16]);
            File.Delete(probe);
        }
        catch (Exception ex) { return (false, "Reachable, but not writable: " + Capitalize(Describe(ex)), 0L); }
        long free = FreeBytes(p);
        string kind = IsUnc(p) ? "Share" : "Folder";
        return (true, free >= 0 ? $"{kind} reachable · writable · {FormatBytes(free)} free" : $"{kind} reachable · writable", Math.Max(0, free));
    });

    /// <summary>Null when the folder exists (or could be created), else a short reason.</summary>
    private static string? Reach(string root)
    {
        try
        {
            if (Directory.Exists(root)) return null;
            Directory.CreateDirectory(root);
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    private string IdleStatus()
    {
        var s = _settings;
        if (!s.ArchiveEnabled || string.IsNullOrWhiteSpace(s.ArchivePath)) return "Archive off";
        int archived = _library.Entries.Count(e => e.IsArchived);
        var parts = new List<string> { archived == 1 ? "1 clip archived" : $"{archived} clips archived" };
        if (_lastMove is DateTime t) parts.Add($"last move {t:HH:mm}");
        if (_problem != null) parts.Add(_problem + ", will retry");
        else if (_reachable) parts.Add(IsUnc(s.ArchivePath) ? "NAS reachable" : "archive reachable");
        return string.Join(" · ", parts);
    }

    private void SetStatus(string status)
    {
        lock (_gate) { if (_status == status) return; _status = status; }
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Warn("archive: Changed handler: " + ex.Message); }
    }

    // ------------------------------------------------------------------ helpers

    private static readonly int[] s_accessCodes = { 5, 86, 1219, 1326, 1327, 1385, 1909 };               // access denied / bad password / credential conflict / logon failure / account restriction / logon type / locked out
    private static readonly int[] s_networkCodes = { 3, 21, 53, 58, 59, 64, 67, 121, 1203, 1231, 1232, 1265, 2250, 1396, 1398 }; // path not found / not ready / bad net path / bad net response / net name deleted / timeout / unreachable / not connected

    private static int Win32Code(Exception ex) => (ex.HResult & 0xFFFF0000) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : -1;

    private static bool IsUnreachable(Exception ex) =>
        ex is DirectoryNotFoundException || ex is DriveNotFoundException || Array.IndexOf(s_networkCodes, Win32Code(ex)) >= 0 ||
        (ex is IOException && Win32Code(ex) == -1 && ex is not FileNotFoundException);

    /// <summary>A short, user-facing reason: "access denied: open the share in Explorer once to sign in", "archive unreachable", "archive is full", …</summary>
    private static string Describe(Exception ex)
    {
        int code = Win32Code(ex);
        if (ex is UnauthorizedAccessException || Array.IndexOf(s_accessCodes, code) >= 0)
            return "access denied: open the share in Explorer once to sign in";
        if (code == 112 || code == 39) return "archive is full";
        if (IsUnreachable(ex)) return "archive unreachable";
        return ex.Message.TrimEnd('.');
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool IsUnc(string? path) => path != null && path.StartsWith(@"\\", StringComparison.Ordinal);

    private const string SameFolderProblem = "archive location is the clips folder";

    private static bool IsUnder(string path, string folder)
    {
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            return string.Equals(dir.TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Two folder spellings that name the same folder (case, trailing backslash, relative parts). Aliases such as a mapped drive are caught per file by <see cref="SameFile"/>.</summary>
    public static bool IsSameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim().Trim('"')).TrimEnd('\\'), Path.GetFullPath(b.Trim().Trim('"')).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>True when the two paths are one file (volume serial + file index), false when they are two files, null when that could not be established.</summary>
    private static unsafe bool? SameFile(string a, string b)
    {
        try
        {
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            BY_HANDLE_FILE_INFORMATION ia, ib;
            if (!PInvoke.GetFileInformationByHandle(new HANDLE(fa.SafeFileHandle.DangerousGetHandle()), &ia)) return null;
            if (!PInvoke.GetFileInformationByHandle(new HANDLE(fb.SafeFileHandle.DangerousGetHandle()), &ib)) return null;
            return ia.dwVolumeSerialNumber == ib.dwVolumeSerialNumber && ia.nFileIndexHigh == ib.nFileIndexHigh && ia.nFileIndexLow == ib.nFileIndexLow;
        }
        catch { return null; }
    }

    /// <summary>A file at the archive path is reused as "our copy" only when its length and its first and last 256 KB match the local file.</summary>
    private static bool LooksLikeCopy(string src, string dest, long length)
    {
        const int Probe = 256 * 1024;
        try
        {
            using var a = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            using var b = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (a.Length != length || b.Length != length) return false;
            int n = (int)Math.Min(Probe, length);
            var ba = new byte[n]; var bb = new byte[n];
            if (!ReadExact(a, ba, 0) || !ReadExact(b, bb, 0) || !ba.AsSpan().SequenceEqual(bb)) return false;
            if (length > n)
            {
                long tail = length - n;
                if (!ReadExact(a, ba, tail) || !ReadExact(b, bb, tail) || !ba.AsSpan().SequenceEqual(bb)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool ReadExact(FileStream fs, byte[] buf, long offset)
    {
        fs.Position = offset;
        int got = 0;
        while (got < buf.Length)
        {
            int n = fs.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private static long FolderBytes(string folder)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            foreach (var f in Directory.EnumerateFiles(folder, "*.mp4"))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    private static string UniqueName(string root, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(root, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Free bytes for the caller at the path (UNC shares included), or -1.</summary>
    public static unsafe long FreeBytes(string path)
    {
        try
        {
            ulong free, total, totalFree;
            string p = path.EndsWith('\\') ? path : path + "\\";
            fixed (char* pp = p)
                if (PInvoke.GetDiskFreeSpaceEx(pp, &free, &total, &totalFree)) return (long)free;
        }
        catch { }
        return -1;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 40) return $"{bytes / (double)(1L << 40):0.0} TB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
        return $"{bytes / 1024.0:0} KB";
    }
}
