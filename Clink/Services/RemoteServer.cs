using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Clink.Services;

/// <summary>What <see cref="RemoteServer"/> needs from Clink; a test brings a fake (no engine, no capture).</summary>
internal interface IRemoteHost
{
    string Version { get; }
    bool IsDev { get; }
    /// <summary>Clink runs as administrator: the pipe's security is made for that (see <see cref="RemoteServer.PipeSddl"/>).</summary>
    bool Elevated { get; }
    /// <summary>Settings → App → Features → Clips is on. Read from a worker thread.</summary>
    bool ClipsEnabled { get; }
    /// <summary>Queues a command (one of the fixed strings of <see cref="RemoteProtocol"/>) for the UI thread. Must not block and must not run it inline.</summary>
    void Run(string command);
}

/// <summary>Limits of a <see cref="RemoteServer"/>; the defaults are the protocol's, a test shortens the timeouts.</summary>
internal sealed class RemoteServerOptions
{
    public TimeSpan HelloTimeout { get; init; } = RemoteProtocol.HelloTimeout;
    public TimeSpan WriteTimeout { get; init; } = RemoteProtocol.WriteTimeout;
    public int MaxClients { get; init; } = RemoteProtocol.MaxClients;
    public int MaxQueued { get; init; } = RemoteProtocol.MaxQueued;
    public double CommandsPerSecond { get; init; } = RemoteProtocol.CommandsPerSecond;
    public double CommandBurst { get; init; } = RemoteProtocol.CommandBurst;
    public double LinesPerSecond { get; init; } = RemoteProtocol.LinesPerSecond;
    public double LineBurst { get; init; } = RemoteProtocol.LineBurst;
    public TimeSpan BackoffStart { get; init; } = RemoteProtocol.BackoffStart;
    public TimeSpan BackoffMax { get; init; } = RemoteProtocol.BackoffMax;
    /// <summary>Whether the client on this pipe handle is in our Windows session (null: asks Windows). A test fakes a client in another session.</summary>
    internal Func<SafePipeHandle, bool>? SessionCheck { get; init; }
    /// <summary>Seconds on a steady clock, for the token buckets and the connect-log throttle (null: the real one). A test drives it.</summary>
    internal Func<double>? Clock { get; init; }
    /// <summary>Test only: the descriptor instances are created with, instead of the computed one (the read-back is still checked against the computed one).</summary>
    internal byte[]? CreateSecurity { get; init; }
}

/// <summary>
/// The duplex status / command pipe: <c>Clink.Remote{suffix}.s{session}</c>, for this user only (see <see cref="PipeSddl"/>). Accept,
/// read and write run on thread-pool tasks; <see cref="Publish"/> and <see cref="PublishEvent"/> only hand work over (never block on a
/// client, never cancel anything inline), so the UI thread can call them. Each client has one latest-state slot (overwritten) and a
/// short queue of events and answers; a client whose queue is full, or whose write blocks for too long, is dropped. Another process
/// that already holds the pipe name is never joined: the server waits (backing off) until the name is free. See
/// <see cref="RemoteProtocol"/> for the messages.
/// </summary>
internal sealed class RemoteServer : IDisposable
{
    private readonly IRemoteHost _host;
    private readonly string _pipeName;
    private readonly RemoteServerOptions _o;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots;   // one per pipe instance: at most MaxClients instances exist, connected or waiting
    private readonly object _gate = new();
    private readonly List<Client> _clients = new();
    private readonly Bucket _commands;       // one for the whole server: a reconnect does not reset it
    private readonly Bucket _connectionLog;   // connect / disconnect lines: 10, then one per 5 s
    private int _connectionLogSkipped;
    private readonly HashSet<string> _loggedErrors = new(StringComparer.Ordinal);
    private RemoteSnapshot? _latest;
    private Task? _loop;
    private string? _lastDrop;
    private int _owned;                      // pipe instances this server made and has not closed yet
    private volatile bool _nameInUse;
    private int _nameInUseRefusals;
    private int _disposed;

    public RemoteServer(IRemoteHost host, string pipeName, RemoteServerOptions? options = null)
    {
        _host = host;
        _pipeName = pipeName;
        _o = options ?? new RemoteServerOptions();
        _slots = new SemaphoreSlim(_o.MaxClients, _o.MaxClients);
        _clock = _o.Clock ?? RealClock;
        _commands = new Bucket(_o.CommandsPerSecond, _o.CommandBurst, _clock);
        _connectionLog = new Bucket(0.2, 10, _clock);
    }

    private readonly Func<double> _clock;
    private static double RealClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Clients connected now (before or after their hello).</summary>
    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    /// <summary>Why the last client left (a short code), or null; for tests and the log.</summary>
    public string? LastDropReason { get { lock (_gate) return _lastDrop; } }

    /// <summary>Another process holds the pipe name (or a pipe of that name that is not ours answered): nothing is served until it is free.</summary>
    public bool NameInUse => _nameInUse;

    /// <summary>How many times an instance was refused because the name was in use (tests: it keeps trying).</summary>
    public int NameInUseRefusals => Volatile.Read(ref _nameInUseRefusals);

    public void Start() => _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();   // every client's token is linked to it, and the accept loop re-checks it before it adds a client
        Client[] clients;
        lock (_gate) clients = _clients.ToArray();
        foreach (var c in clients) c.Drop("shutdown");
        try { _loop?.Wait(1000); } catch (Exception) { }
        var deadline = Stopwatch.StartNew();
        while (ClientCount > 0 && deadline.ElapsedMilliseconds < 1000) Thread.Sleep(10);
    }

    // ------------------------------------------------------------------ security

    private const uint PipeReadWrite = (uint)(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);
    private const uint PipeCreateInstance = (uint)PipeAccessRights.CreateNewInstance;
    private const uint FileAllAccess = 0x1F01FF;
    private const uint ReadControl = 0x20000;

    /// <summary>
    /// The pipe's security descriptor (SDDL), for every instance:
    /// <list type="bullet">
    /// <item>Owner: the user SID (<c>WindowsIdentity.User</c>, not <c>.Owner</c>: for an elevated token the owner is BUILTIN\Administrators).</item>
    /// <item>NETWORK is denied everything.</item>
    /// <item>OWNER RIGHTS gets READ_CONTROL only, so the owner has no implicit WRITE_DAC.</item>
    /// <item>The user reads and writes. Not elevated, it also gets CreateNewInstance (every further instance is checked against it). Elevated,
    /// CreateNewInstance goes to BUILTIN\Administrators instead (only the elevated Clink has that group enabled), so a process of the same
    /// user that is not elevated can connect but cannot add instances of its own to the pipe.</item>
    /// <item>Mandatory label Medium, no-write-up and no-read-up: a Low or Untrusted process of the same user cannot open it at all.</item>
    /// </list>
    /// </summary>
    internal static string PipeSddl(SecurityIdentifier user, bool elevated)
    {
        string u = user.Value;
        var sb = new StringBuilder();
        sb.Append("O:").Append(u).Append("D:P");
        sb.Append("(D;;0x").Append(FileAllAccess.ToString("x")).Append(";;;NU)");
        sb.Append("(A;;0x").Append(ReadControl.ToString("x")).Append(";;;OW)");
        if (elevated)
        {
            sb.Append("(A;;0x").Append(PipeReadWrite.ToString("x")).Append(";;;").Append(u).Append(')');
            sb.Append("(A;;0x").Append(PipeCreateInstance.ToString("x")).Append(";;;BA)");
        }
        else sb.Append("(A;;0x").Append((PipeReadWrite | PipeCreateInstance).ToString("x")).Append(";;;").Append(u).Append(')');
        sb.Append("S:(ML;;NWNR;;;ME)");
        return sb.ToString();
    }

    /// <summary>The descriptor for the current user, as the self-relative bytes CreateNamedPipe takes.</summary>
    internal static byte[] PipeSecurity(bool elevated)
    {
        using var id = WindowsIdentity.GetCurrent();
        var user = id.User ?? throw new InvalidOperationException("no user SID");
        return SddlToBinary(PipeSddl(user, elevated));
    }

    /// <summary>Windows' own conversion, so the label reaches CreateNamedPipe exactly as written (no managed ACL class in between; a test reads it back).</summary>
    internal static byte[] SddlToBinary(string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr sd, out uint size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(sd, bytes, 0, (int)size);
            return bytes;
        }
        finally { LocalFree(sd); }
    }

    private const uint PipeAccessDuplex = 0x3;
    private const uint PipeRejectRemoteClients = 0x8;   // PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT are 0
    private const int ErrorAccessDenied = 5;
    private const int ErrorPipeBusy = 231;

    /// <summary>
    /// One pipe instance with <paramref name="security"/>. <paramref name="first"/>: FILE_FLAG_FIRST_PIPE_INSTANCE, so a pipe of that name
    /// that already exists (another process made it) is never joined: Windows refuses with ERROR_ACCESS_DENIED instead. Not first: the
    /// instance joins our pipe and has its security (the caller checks that with <see cref="ReadSecurity"/>). Throws Win32Exception.
    /// </summary>
    internal static unsafe NamedPipeServerStream CreateInstance(string name, byte[] security, int maxInstances, bool first)
    {
        uint openMode = PipeAccessDuplex | (uint)PipeOptions.Asynchronous | (first ? (uint)PipeOptions.FirstPipeInstance : 0);
        SafePipeHandle handle;
        fixed (byte* sd = security)
        {
            var sa = new SecurityAttributes { Length = sizeof(SecurityAttributes), Descriptor = (IntPtr)sd, Inherit = 0 };
            handle = CreateNamedPipe(@"\\.\pipe\" + name, openMode, PipeRejectRemoteClients, (uint)maxInstances, 4096, 4096, 0, ref sa);
        }
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(err);
        }
        try { return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle); }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>What Windows holds for a pipe instance: owner, DACL and mandatory label (no SeSecurityPrivilege needed for those).</summary>
    internal static RawSecurityDescriptor ReadSecurity(SafePipeHandle handle)
    {
        const int SeKernelObject = 6;
        const uint Owner = 0x1, Dacl = 0x4, Label = 0x10;
        uint err = GetSecurityInfo(handle, SeKernelObject, Owner | Dacl | Label, out _, out _, out _, out _, out IntPtr sd);
        if (err != 0) throw new Win32Exception((int)err);
        try
        {
            int len = (int)GetSecurityDescriptorLength(sd);
            var bytes = new byte[len];
            Marshal.Copy(sd, bytes, 0, len);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally { LocalFree(sd); }
    }

    /// <summary>Null when <paramref name="actual"/> has exactly the owner, DACL entries and mandatory label of <paramref name="expected"/>; otherwise what differs.</summary>
    internal static string? SecurityDifference(RawSecurityDescriptor expected, RawSecurityDescriptor actual)
    {
        if (actual.Owner == null || actual.Owner != expected.Owner) return "owner";
        if (!SameAces(Aces(expected.DiscretionaryAcl, labelsOnly: false), Aces(actual.DiscretionaryAcl, labelsOnly: false))) return "dacl";
        if (!SameAces(Aces(expected.SystemAcl, labelsOnly: true), Aces(actual.SystemAcl, labelsOnly: true))) return "label";
        return null;
    }

    private const byte MandatoryLabelAce = 0x11;

    private static List<byte[]> Aces(RawAcl? acl, bool labelsOnly)
    {
        var list = new List<byte[]>();
        if (acl == null) return list;
        foreach (GenericAce ace in acl)
        {
            if (labelsOnly && (byte)ace.AceType != MandatoryLabelAce) continue;
            var b = new byte[ace.BinaryLength];
            ace.GetBinaryForm(b, 0);
            list.Add(b);
        }
        return list;
    }

    private static bool SameAces(List<byte[]> a, List<byte[]> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, ref SecurityAttributes security);

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr sd, out uint size);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeHandle handle, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr sd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    private bool SessionOk(SafePipeHandle handle)
    {
        if (_o.SessionCheck is { } check) return check(handle);
        return SameSession(handle, (uint)AppController.CurrentSessionId);
    }

    /// <summary>The client on this pipe handle is in session <paramref name="ours"/>. Fails closed: an error asking Windows means no.</summary>
    internal static bool SameSession(SafePipeHandle handle, uint ours)
    {
        try { return GetNamedPipeClientSessionId(handle, out uint sid) && sid == ours; }
        catch (Exception) { return false; }   // cannot tell: not ours
    }

    // ------------------------------------------------------------------ accept

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        byte[] security;
        RawSecurityDescriptor expected;
        try
        {
            security = PipeSecurity(_host.Elevated);
            expected = new RawSecurityDescriptor(security, 0);
        }
        catch (Exception ex) { Log.Warn("remote: pipe security: " + ex.Message); return; }
        TimeSpan delay = TimeSpan.Zero;
        while (!ct.IsCancellationRequested)
        {
            bool haveSlot = false, counted = false, inUse = false, ownMismatch = false;
            string? problem = null, differs = null;
            NamedPipeServerStream? pipe = null;
            try
            {
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                haveSlot = true;
                // No instance of ours exists: this one must be the pipe's first, or another process got the name first.
                bool first = Volatile.Read(ref _owned) == 0;
                try { pipe = CreateInstance(_pipeName, _o.CreateSecurity ?? security, _o.MaxClients, first); }
                catch (Win32Exception ex) when (first && ex.NativeErrorCode is ErrorAccessDenied or ErrorPipeBusy) { inUse = true; }
                if (pipe != null)
                {
                    Interlocked.Increment(ref _owned);
                    counted = true;
                    // Whatever the flag said, an instance that does not have exactly our security is not ours to serve on.
                    // A first instance we just made that differs has our own descriptor (not a squatter); any other instance that differs joined someone else's pipe.
                    differs = SecurityDifference(expected, ReadSecurity(pipe.SafePipeHandle));
                    if (differs != null) { if (first) ownMismatch = true; else inUse = true; }
                }
                if (!inUse && !ownMismatch)
                {
                    NameFree();
                    delay = TimeSpan.Zero;
                    await pipe!.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var client = new Client(this, pipe);
                    bool added;
                    lock (_gate)
                    {
                        added = !ct.IsCancellationRequested;   // Dispose took its list of clients already: this one must not slip past it
                        if (added) _clients.Add(client);
                    }
                    if (added)
                    {
                        pipe = null;          // the client owns the instance, its count and the slot now
                        counted = false;
                        haveSlot = false;
                        client.Start();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { problem = ex.Message; }
            finally
            {
                if (pipe != null) { try { pipe.Dispose(); } catch (Exception) { } }
                if (counted) Interlocked.Decrement(ref _owned);
                if (haveSlot) _slots.Release();
            }
            if (ownMismatch) LogOnce("remote: instance security differs: " + differs);
            else if (inUse) NameTaken(differs);
            else if (problem != null) LogOnce("remote: accept: " + problem);
            else continue;
            delay = delay == TimeSpan.Zero ? _o.BackoffStart : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _o.BackoffMax.Ticks));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private void NameTaken(string? differs)
    {
        Interlocked.Increment(ref _nameInUseRefusals);
        if (_nameInUse) return;
        _nameInUse = true;
        Log.Warn("remote: pipe name in use (" + _pipeName + (differs != null ? ", security differs: " + differs : "") + "): not serving until it is free");
    }

    private void NameFree()
    {
        if (!_nameInUse) return;
        _nameInUse = false;
        Log.Info("remote: pipe name free again (" + _pipeName + "): serving");
    }

    /// <summary>Each distinct problem once per server (the accept loop retries, backing off, and must not fill the log).</summary>
    private void LogOnce(string message)
    {
        lock (_loggedErrors)
        {
            if (_loggedErrors.Count >= 32 || !_loggedErrors.Add(message)) return;
        }
        Log.Warn(message);
    }

    /// <summary>A connect / disconnect line: the first 10 as they come, then one per 5 s; the next one written says how many were left out.</summary>
    private void LogConnection(string message)
    {
        int skipped;
        lock (_connectionLog)
        {
            if (!_connectionLog.TryTake()) { _connectionLogSkipped++; return; }
            skipped = _connectionLogSkipped;
            _connectionLogSkipped = 0;
        }
        Log.Info(skipped > 0 ? message + " (" + skipped + " connect / disconnect lines left out)" : message);
    }

    // ------------------------------------------------------------------ publishing (any thread, never blocks on a client)

    /// <summary>The state changed? Sends it to every client that has said hello, unless it equals the last one published.</summary>
    public void Publish(RemoteSnapshot snapshot)
    {
        lock (_gate)
        {
            if (Equals(_latest, snapshot)) return;
            _latest = snapshot;
            foreach (var c in _clients) c.SetState(snapshot);
        }
    }

    /// <summary>Queues one event line (built by <see cref="RemoteProtocol"/>) for every client that has said hello. A client whose queue is full is dropped, after the lock.</summary>
    public void PublishEvent(string line)
    {
        List<Client>? full = null;
        lock (_gate)
            foreach (var c in _clients)
                if (!c.Enqueue(line)) (full ??= new()).Add(c);
        if (full != null) foreach (var c in full) c.Drop("queueFull");
    }

    // ------------------------------------------------------------------ one client

    private sealed class LineTooLongException : Exception { }
    private sealed class FloodException : Exception { }

    /// <summary>
    /// Reads UTF-8 lines; a line longer than <see cref="RemoteProtocol.MaxLineBytes"/> (not counting its CR LF or LF) throws while it is
    /// read, never after buffering it all.
    /// </summary>
    private sealed class LineReader
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[1024];
        private readonly byte[] _acc = new byte[RemoteProtocol.MaxLineBytes + 1];   // + the CR of a CR LF
        private int _pos, _len, _accLen;

        public LineReader(Stream s) => _s = s;

        /// <summary>The next line without its line break, or null at the end of the stream.</summary>
        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                while (_pos < _len)
                {
                    byte b = _buf[_pos++];
                    if (b == (byte)'\n')
                    {
                        int n = _accLen;
                        _accLen = 0;
                        if (n > 0 && _acc[n - 1] == (byte)'\r') n--;
                        if (n > RemoteProtocol.MaxLineBytes) throw new LineTooLongException();
                        return Encoding.UTF8.GetString(_acc, 0, n);
                    }
                    // After the limit only the CR of a CR LF may come.
                    if (_accLen >= RemoteProtocol.MaxLineBytes && (_accLen > RemoteProtocol.MaxLineBytes || b != (byte)'\r')) throw new LineTooLongException();
                    _acc[_accLen++] = b;
                }
                _pos = 0;
                _len = await _s.ReadAsync(_buf.AsMemory(), ct).ConfigureAwait(false);
                if (_len == 0) return null;
            }
        }
    }

    /// <summary>A token bucket; thread-safe (the server's command bucket is shared by every client).</summary>
    private sealed class Bucket
    {
        private readonly double _rate, _burst;
        private double _tokens;
        private readonly Func<double> _clock;
        private double _at;

        public Bucket(double rate, double burst, Func<double> clock) { _rate = rate; _burst = burst; _tokens = burst; _clock = clock; _at = clock(); }

        public bool TryTake()
        {
            lock (this)
            {
                double now = _clock();
                _tokens = Math.Min(_burst, _tokens + Math.Max(0, now - _at) * _rate);
                _at = now;
                if (_tokens < 1) return false;
                _tokens -= 1;
                return true;
            }
        }
    }

    private sealed class Client
    {
        private readonly RemoteServer _s;
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _cts;   // linked to the server's: Dispose ends every client
        private readonly object _q = new();
        private readonly Queue<string> _out = new();
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly Bucket _lines;
        private RemoteSnapshot? _state;
        private bool _stateDirty, _ready;
        private string? _reason;

        public Client(RemoteServer s, NamedPipeServerStream pipe)
        {
            _s = s;
            _pipe = pipe;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(s._cts.Token);
            _lines = new Bucket(s._o.LinesPerSecond, s._o.LineBurst, s._clock);
        }

        public void Start() => _ = Task.Run(RunAsync);

        /// <summary>Ends the connection; the first reason given is kept. Never runs the cancellation inline (the caller may be the UI thread): its callbacks run on the thread pool.</summary>
        public void Drop(string reason)
        {
            Interlocked.CompareExchange(ref _reason, reason, null);
            try { _ = _cts.CancelAsync(); } catch (ObjectDisposedException) { }
        }

        public void SetState(RemoteSnapshot s)
        {
            lock (_q)
            {
                if (!_ready) return;
                _state = s;
                _stateDirty = true;
            }
            Wake();
        }

        /// <summary>Queues a line for a client that said hello; false when its queue is full (the caller drops it, outside its locks).</summary>
        public bool Enqueue(string line)
        {
            lock (_q)
            {
                if (!_ready) return true;
                if (_out.Count >= _s._o.MaxQueued) return false;
                _out.Enqueue(line);
            }
            Wake();
            return true;
        }

        private void Answer(string line)
        {
            if (!Enqueue(line)) Drop("queueFull");
        }

        private void Wake()
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
        }

        /// <summary>The next line, counted against the connection's line limit (every line, before and after hello).</summary>
        private async Task<string?> NextLineAsync(LineReader reader, CancellationToken ct)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line != null && !_lines.TryTake()) throw new FloodException();
            return line;
        }

        private async Task RunAsync()
        {
            Task? writer = null;
            try
            {
                if (!_s.SessionOk(_pipe.SafePipeHandle))
                {
                    await WriteAsync(RemoteProtocol.Bye("session")).ConfigureAwait(false);
                    Drop("session");
                    return;
                }
                var reader = new LineReader(_pipe);
                RemoteMessage? hello = null;
                using (var hct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    hct.CancelAfter(_s._o.HelloTimeout);   // one deadline for the whole hello, so a trickle of other lines does not extend it
                    try
                    {
                        while (hello == null)
                        {
                            string? line = await NextLineAsync(reader, hct.Token).ConfigureAwait(false);
                            if (line == null) { Drop("closed"); return; }
                            var m = RemoteProtocol.Parse(line);
                            if (m is { Type: "hello" }) hello = m;
                        }
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { Drop("helloTimeout"); return; }
                }
                if (hello.Proto != RemoteProtocol.Proto)
                {
                    await WriteAsync(RemoteProtocol.Bye("proto", RemoteProtocol.Proto)).ConfigureAwait(false);
                    Drop("proto");
                    return;
                }
                string who = (RemoteProtocol.Sanitize(hello.Client) + " " + RemoteProtocol.Sanitize(hello.Version)).Trim();
                _s.LogConnection("remote: client connected (" + (who.Length > 0 ? who : "unnamed") + ")");
                lock (_s._gate)
                {
                    lock (_q)
                    {
                        _out.Enqueue(RemoteProtocol.Welcome(_s._host.Version, _s._host.IsDev, _s._host.Elevated));
                        _state = _s._latest;
                        _stateDirty = _state != null;
                        _ready = true;
                    }
                }
                Wake();
                writer = Task.Run(WriterAsync);
                while (true)
                {
                    string? line = await NextLineAsync(reader, _cts.Token).ConfigureAwait(false);
                    if (line == null) { Drop("closed"); return; }
                    Dispatch(RemoteProtocol.Parse(line));
                }
            }
            catch (LineTooLongException) { Drop("lineTooLong"); }
            catch (FloodException) { Drop("flood"); }
            catch (OperationCanceledException) { }
            catch (Exception) { Drop("io"); }
            finally { await FinishAsync(writer).ConfigureAwait(false); }
        }

        private void Dispatch(RemoteMessage? m)
        {
            if (m == null) return;
            switch (m.Type)
            {
                case "ping":
                    Answer(RemoteProtocol.Pong);
                    break;
                case "cmd":
                    if (m.Id is not long id) return;   // nothing to answer to
                    string? error;
                    string command = "";
                    if (!_s._commands.TryTake()) error = "rateLimited";
                    else
                    {
                        error = RemoteProtocol.Resolve(m.Verb, out command);
                        if (error == null && !_s._host.ClipsEnabled && RemoteProtocol.NeedsClips(m.Verb!)) error = "clipsOff";
                    }
                    if (error == null)
                    {
                        try { _s._host.Run(command); }
                        catch (Exception) { error = "notAllowed"; }   // the UI is gone (Clink is quitting)
                    }
                    Answer(RemoteProtocol.Ack(id, error));
                    break;
            }
        }

        private async Task WriterAsync()
        {
            var ct = _cts.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                    while (true)
                    {
                        string? line = null;
                        RemoteSnapshot? snap = null;
                        lock (_q)
                        {
                            if (_out.Count > 0) line = _out.Dequeue();
                            else if (_stateDirty) { snap = _state; _stateDirty = false; }
                        }
                        if (line == null && snap == null) break;
                        await WriteAsync(line ?? RemoteProtocol.State(snap!)).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Drop("io"); }
        }

        /// <summary>One line to the client; a write that does not finish within the write timeout drops the client.</summary>
        private async Task WriteAsync(string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            using var wct = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            wct.CancelAfter(_s._o.WriteTimeout);
            try { await _pipe.WriteAsync(bytes, wct.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { Drop("writeTimeout"); throw; }
        }

        private async Task FinishAsync(Task? writer)
        {
            Drop("closed");   // keeps an earlier reason
            if (writer != null) await Task.WhenAny(writer, Task.Delay(1000)).ConfigureAwait(false);
            string reason = _reason ?? "closed";
            lock (_s._gate)
            {
                _s._clients.Remove(this);
                _s._lastDrop = reason;
            }
            try { _pipe.Dispose(); } catch (Exception) { }
            Interlocked.Decrement(ref _s._owned);
            _s._slots.Release();
            // Unlinks it from the server's token (a long session sees many connections). A writer still stuck past its second keeps it.
            if (writer == null || writer.IsCompleted) _cts.Dispose();
            _s.LogConnection("remote: client disconnected (" + reason + ")");
        }
    }
}
