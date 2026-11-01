using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The remote status pipe against a real pipe and a fake host (no engine, no capture). Waits use deadlines, never fixed sleeps.</summary>
internal static class RemoteServerTests
{
    private sealed class FakeHost : IRemoteHost
    {
        private readonly List<string> _commands = new();
        public string Version => "9.9.9";
        public bool IsDev => true;
        public bool Elevated => false;
        public volatile bool Clips = true;
        public bool ClipsEnabled => Clips;
        public void Run(string command) { lock (_commands) _commands.Add(command); }
        public string[] Commands { get { lock (_commands) return _commands.ToArray(); } }
    }

    /// <summary>A client: reads on a background task into a queue, writes synchronously.</summary>
    private sealed class TestClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly BlockingCollection<string?> _lines = new();

        public TestClient(string name, bool read = true, int connectMs = 5000)
        {
            _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(connectMs);
            if (read) _ = Task.Run(ReadLoop);
        }

        private async Task ReadLoop()
        {
            try
            {
                using var r = new StreamReader(_pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                while (true)
                {
                    string? l = await r.ReadLineAsync().ConfigureAwait(false);
                    _lines.Add(l);
                    if (l == null) return;
                }
            }
            catch (Exception) { try { _lines.Add(null); } catch (Exception) { } }
        }

        public void SendRaw(byte[] bytes) { if (!_pipe.WriteAsync(bytes).AsTask().Wait(5000)) Check.Fail("write timed out"); }
        public void Send(string line) => SendRaw(Encoding.UTF8.GetBytes(line + "\n"));
        public void Hello(int proto = 1) => Send("{\"t\":\"hello\",\"proto\":" + proto + ",\"client\":\"unittest\",\"v\":\"1\"}");
        public void Cmd(long id, string verb) => Send("{\"t\":\"cmd\",\"id\":" + id + ",\"verb\":" + JsonSerializer.Serialize(verb) + "}");

        /// <summary>The next line; null when the server closed. Fails the test if nothing comes in time.</summary>
        public string? Next(int ms = 5000)
        {
            if (!_lines.TryTake(out string? l, ms)) Check.Fail("no line within " + ms + " ms");
            return l;
        }

        public JsonElement NextJson(int ms = 5000)
        {
            string? l = Next(ms);
            Check.NotNull(l, "the server closed");
            return JsonDocument.Parse(l!).RootElement.Clone();
        }

        /// <summary>The server closed the connection (lines still on the way are skipped) within the time.</summary>
        public bool Closed(int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                int left = ms - (int)sw.ElapsedMilliseconds;
                if (left <= 0 || !_lines.TryTake(out string? l, left)) return false;
                if (l == null) return true;
            }
        }

        /// <summary>The client has said hello and got its welcome and first state.</summary>
        public void Join(bool state = true)
        {
            Hello();
            Check.Equal("welcome", NextJson().GetProperty("t").GetString());
            if (state) Check.Equal("state", NextJson().GetProperty("t").GetString());
        }

        public void Dispose() { try { _pipe.Dispose(); } catch (Exception) { } }
    }

    private sealed class Rig : IDisposable
    {
        public readonly FakeHost Host = new();
        public readonly RemoteServer Server;
        public readonly string Name;
        private readonly TempDir _keys = new();
        /// <summary>The key folder of this rig: always a folder under the temp folder (never the real %LOCALAPPDATA%\Clink\remote); made by the server.</summary>
        public string KeyDir => _keys.File("remote");
        public string KeyPath => RemoteKeyFile.PathFor(KeyDir, Name);

        /// <summary>The key file (written by the accept loop a moment after Start): waits for it, and returns its path.</summary>
        public string WaitForKey()
        {
            Check.True(Until(() => File.Exists(KeyPath)), "the key file was published");
            return KeyPath;
        }

        public Rig(RemoteServerOptions? options = null, RemoteSnapshot? first = null, bool keyFile = true)
        {
            AppController.InstanceSuffix = ".dev.unittest-" + Guid.NewGuid().ToString("N");
            Name = AppController.RemotePipeName;
            Server = new RemoteServer(Host, Name, (options ?? new RemoteServerOptions()) with { KeyDir = keyFile ? KeyDir : null });
            if (first != null) Server.Publish(first);
            Server.Start();
        }

        public TestClient Connect(bool read = true) => new(Name, read);

        public void Dispose()
        {
            Server.Dispose();
            _keys.Dispose();
        }
    }

    private static bool Until(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>A clock a test moves by hand (seconds); the buckets and the connect-log throttle read it, so no machine speed matters.</summary>
    private sealed class FakeClock
    {
        private long _ms;
        public double Now => Interlocked.Read(ref _ms) / 1000.0;
        public void Advance(double seconds) => Interlocked.Add(ref _ms, (long)(seconds * 1000));
    }

    private static RemoteSnapshot Sample(string state = "recording", int s = 12) => RemoteProtocolTests.Sample(state, s);

    // ------------------------------------------------------------------ the conversation

    [UnitTest]
    public static void Remote_hello_gets_welcome_then_the_full_state()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Hello();
        var welcome = c.NextJson();
        Check.Equal("welcome", welcome.GetProperty("t").GetString());
        Check.Equal(1, welcome.GetProperty("proto").GetInt32());
        Check.Equal("9.9.9", welcome.GetProperty("version").GetString());
        Check.True(welcome.GetProperty("dev").GetBoolean());
        Check.False(welcome.GetProperty("elevated").GetBoolean());
        Check.Equal(7, welcome.GetProperty("verbs").GetArrayLength());
        var state = c.NextJson();
        Check.Equal("state", state.GetProperty("t").GetString());
        Check.Equal("recording", state.GetProperty("state").GetString());
        Check.Equal(12, state.GetProperty("rec").GetProperty("s").GetInt32());
    }

    [UnitTest]
    public static void Remote_identical_snapshots_send_nothing_and_a_changed_one_sends_once()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        rig.Server.Publish(Sample());   // equal values, a new instance
        rig.Server.Publish(Sample());
        c.Send("{\"t\":\"ping\"}");
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString());   // a state in between would come first
        rig.Server.Publish(Sample(s: 13));
        var next = c.NextJson();
        Check.Equal("state", next.GetProperty("t").GetString());
        Check.Equal(13, next.GetProperty("rec").GetProperty("s").GetInt32());
        rig.Server.Publish(Sample(s: 13));
        c.Send("{\"t\":\"ping\"}");
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString());
    }

    [UnitTest]
    public static void Remote_a_command_is_acked_and_runs_the_fixed_command()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        c.Cmd(41, "clip");
        var ack = c.NextJson();
        Check.Equal("ack", ack.GetProperty("t").GetString());
        Check.Equal(41L, ack.GetProperty("id").GetInt64());
        Check.True(ack.GetProperty("ok").GetBoolean());
        Check.True(Until(() => rig.Host.Commands.Length == 1), "the command reached the host");
        Check.Equal("clip", rig.Host.Commands[0]);
    }

    [UnitTest]
    public static void Remote_unknown_and_edit_verbs_are_refused_and_never_reach_the_host()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        string[] sent = { "format", "screenshot-edit", "screenshot-edit-last", "screenshot-edit C:\\x.png" };
        string[] expected = { "unknownVerb", "notAllowed", "notAllowed", "notAllowed" };
        for (int i = 0; i < sent.Length; i++)
        {
            c.Cmd(i + 1, sent[i]);
            var ack = c.NextJson();
            Check.False(ack.GetProperty("ok").GetBoolean(), sent[i]);
            Check.Equal(expected[i], ack.GetProperty("error").GetString(), sent[i]);
        }
        c.Send("{\"t\":\"ping\"}");   // after the acks: the host has been told by now if it was going to be
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString());
        Check.Equal(0, rig.Host.Commands.Length);
    }

    [UnitTest]
    public static void Remote_clip_verbs_say_clipsOff_while_clips_is_off_and_show_still_runs()
    {
        using var rig = new Rig(first: Sample("clipsOff"));
        rig.Host.Clips = false;
        using var c = rig.Connect();
        c.Join();
        c.Cmd(1, "clip");
        Check.Equal("clipsOff", c.NextJson().GetProperty("error").GetString());
        c.Cmd(2, "show");
        Check.True(c.NextJson().GetProperty("ok").GetBoolean());
        Check.True(Until(() => rig.Host.Commands.Length == 1));
        Check.Equal("show", rig.Host.Commands[0]);
    }

    [UnitTest]
    public static void Remote_ping_is_answered_and_unknown_lines_are_ignored()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        c.Send("this is not json");
        c.Send("{\"t\":\"somethingNew\",\"x\":[1,2,3]}");
        c.Send("{\"t\":\"cmd\",\"verb\":\"clip\"}");   // no id: nothing to answer
        c.Send("{\"t\":\"ping\",\"extra\":true}");
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString());
        Check.Equal(0, rig.Host.Commands.Length);
    }

    [UnitTest]
    public static void Remote_a_command_sent_before_hello_does_not_run()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Cmd(1, "clip");
        c.Hello();
        Check.Equal("welcome", c.NextJson().GetProperty("t").GetString());   // no ack before it
        Check.Equal("state", c.NextJson().GetProperty("t").GetString());
        c.Send("{\"t\":\"ping\"}");
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString());
        Check.Equal(0, rig.Host.Commands.Length);
    }

    [UnitTest]
    public static void Remote_events_reach_a_client_that_said_hello()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        rig.Server.PublishEvent(RemoteProtocol.ClipSaved(30));
        rig.Server.PublishEvent(RemoteProtocol.ShareDone("uploaded"));
        var a = c.NextJson();
        Check.Equal("clipSaved", a.GetProperty("t").GetString());
        Check.Equal(30, a.GetProperty("seconds").GetInt32());
        Check.Equal("uploaded", c.NextJson().GetProperty("outcome").GetString());
    }

    // ------------------------------------------------------------------ refusals and limits

    [UnitTest]
    public static void Remote_a_protocol_other_than_1_gets_bye_and_is_closed()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Hello(proto: 2);
        var bye = c.NextJson();
        Check.Equal("bye", bye.GetProperty("t").GetString());
        Check.Equal("proto", bye.GetProperty("reason").GetString());
        Check.Equal(1, bye.GetProperty("max").GetInt32());
        Check.True(c.Closed(), "closed after the bye");
    }

    [UnitTest]
    public static void Remote_a_client_in_another_session_gets_bye_and_is_closed()
    {
        using var rig = new Rig(new RemoteServerOptions { SessionCheck = _ => false }, Sample());
        using var c = rig.Connect();
        var bye = c.NextJson();
        Check.Equal("bye", bye.GetProperty("t").GetString());
        Check.Equal("session", bye.GetProperty("reason").GetString());
        Check.True(c.Closed());
    }

    [UnitTest]
    public static void Remote_a_line_over_4096_bytes_closes_the_connection()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        c.SendRaw(Encoding.ASCII.GetBytes(new string('a', RemoteProtocol.MaxLineBytes + 1)));   // no line break: it must be refused while it is read
        Check.True(c.Closed(), "closed");
        Check.True(Until(() => rig.Server.ClientCount == 0));
        Check.Equal("lineTooLong", rig.Server.LastDropReason);
    }

    [UnitTest]
    public static void Remote_a_line_of_exactly_4096_bytes_is_still_read()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        string head = "{\"t\":\"ping\",\"pad\":\"";
        string tail = "\"}";
        c.Send(head + new string('x', RemoteProtocol.MaxLineBytes - head.Length - tail.Length) + tail);
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString());
    }

    [UnitTest]
    public static void Remote_the_CR_of_a_CR_LF_does_not_count_toward_the_4096_bytes()
    {
        using var rig = new Rig(first: Sample());
        using var c = rig.Connect();
        c.Join();
        string head = "{\"t\":\"ping\",\"pad\":\"";
        string tail = "\"}";
        string full = head + new string('x', RemoteProtocol.MaxLineBytes - head.Length - tail.Length) + tail;
        c.SendRaw(Encoding.ASCII.GetBytes(full + "\r\n"));
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString(), "4096 bytes + CR LF");
        c.SendRaw(Encoding.ASCII.GetBytes("{\"t\":\"ping\"}\r\n"));
        Check.Equal("pong", c.NextJson().GetProperty("t").GetString(), "a short CR LF line");
        c.SendRaw(Encoding.ASCII.GetBytes(full + "\rx\n"));   // a CR that is not a line break is a 4097th byte
        Check.True(c.Closed(), "closed");
        Check.True(Until(() => rig.Server.ClientCount == 0));
        Check.Equal("lineTooLong", rig.Server.LastDropReason);
    }

    [UnitTest]
    public static void Remote_no_hello_in_time_closes_the_connection()
    {
        using var rig = new Rig(new RemoteServerOptions { HelloTimeout = TimeSpan.FromMilliseconds(300) }, Sample());
        using var c = rig.Connect();
        Check.True(c.Closed(), "closed");
        Check.True(Until(() => rig.Server.ClientCount == 0));
        Check.Equal("helloTimeout", rig.Server.LastDropReason);
    }

    [UnitTest]
    public static void Remote_a_trickle_of_other_lines_does_not_extend_the_hello_deadline()
    {
        using var rig = new Rig(new RemoteServerOptions { HelloTimeout = TimeSpan.FromMilliseconds(500) }, Sample());
        using var c = rig.Connect();
        var sw = Stopwatch.StartNew();
        bool closed = false;
        while (!closed && sw.ElapsedMilliseconds < 5000)
        {
            try { c.Send("{\"t\":\"ping\"}"); } catch (Exception) { break; }   // a line every 100 ms: well under the line limit
            closed = c.Closed(100);
        }
        Check.True(closed || c.Closed(), "closed");
        Check.True(sw.ElapsedMilliseconds < 3000, "at the hello deadline, not later: " + sw.ElapsedMilliseconds + " ms");
        Check.True(Until(() => rig.Server.ClientCount == 0));
        Check.Equal("helloTimeout", rig.Server.LastDropReason);
    }

    [UnitTest]
    public static void Remote_connect_lines_name_the_client_safely_and_are_throttled()
    {
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        string[] Remote() { lock (lines) return lines.Where(l => l.Contains(" remote: client ", StringComparison.Ordinal)).ToArray(); }
        var clock = new FakeClock();   // frozen: no token comes back by itself
        using var rig = new Rig(new RemoteServerOptions { Clock = () => clock.Now }, Sample());
        Log.Line += Collect;
        try
        {
            using (var c = rig.Connect())
            {
                c.Send("{\"t\":\"hello\",\"proto\":1,\"client\":\"deck\\nINFO forged\",\"v\":\"0.1\"}");
                Check.Equal("welcome", c.NextJson().GetProperty("t").GetString());
                Check.True(Until(() => Remote().Length == 1));
                Check.Contains(Remote()[0], "remote: client connected (deck?INFO forged 0.1)", "no line break of the client's reaches the log");
            }
            Check.True(Until(() => rig.Server.ClientCount == 0));
            for (int i = 0; i < 12; i++)
            {
                using var c = rig.Connect();
                c.Join();
            }
            Check.True(Until(() => rig.Server.ClientCount == 0));
            Check.True(Until(() => Remote().Length == 10), "26 connect / disconnect lines, exactly the first 10 written: " + Remote().Length);
            Check.Equal(10, Remote().Length, "no more while the clock stands still");
            clock.Advance(5);   // one token back: the next line says how many were left out
            using (var again = rig.Connect()) again.Join();
            Check.True(Until(() => Remote().Any(l => l.Contains("connect / disconnect lines left out", StringComparison.Ordinal))), "the line after the pause counts the ones left out");
        }
        finally { Log.Line -= Collect; }
    }

    [UnitTest]
    public static void Remote_dispose_with_clients_connected_closes_them_and_returns()
    {
        var rig = new Rig(first: Sample());
        using var a = rig.Connect();
        using var b = rig.Connect();
        using var waiting = rig.Connect();   // connected, no hello yet
        a.Join();
        b.Join();
        Check.True(Until(() => rig.Server.ClientCount == 3));
        var sw = Stopwatch.StartNew();
        rig.Dispose();
        Check.True(sw.ElapsedMilliseconds < 4000, "Dispose returned in " + sw.ElapsedMilliseconds + " ms");
        Check.True(Until(() => rig.Server.ClientCount == 0), "every client is gone");
        Check.True(a.Closed(), "a closed");
        Check.True(b.Closed(), "b closed");
        Check.True(waiting.Closed(), "the one before its hello closed");
        Check.Throws<TimeoutException>(() => { using var late = new TestClient(rig.Name, connectMs: 300); }, "nothing accepts after Dispose");
        rig.Dispose();   // twice is fine
    }

    [UnitTest]
    public static void Remote_a_fifth_client_is_refused_until_one_leaves()
    {
        using var rig = new Rig(first: Sample());
        var clients = new List<TestClient>();
        try
        {
            for (int i = 0; i < 4; i++) { var c = rig.Connect(); clients.Add(c); c.Join(); }
            Check.Equal(4, rig.Server.ClientCount);
            Check.Throws<TimeoutException>(() => { using var fifth = new TestClient(rig.Name, connectMs: 400); }, "the fifth cannot connect");
            clients[0].Dispose();
            Check.True(Until(() => rig.Server.ClientCount == 3), "the first left");
            using var again = new TestClient(rig.Name, connectMs: 5000);
            again.Join();
        }
        finally { foreach (var c in clients) c.Dispose(); }
    }

    [UnitTest]
    public static void Remote_commands_are_rate_limited()
    {
        var clock = new FakeClock();
        using var rig = new Rig(new RemoteServerOptions { Clock = () => clock.Now }, Sample());
        using var c = rig.Connect();
        c.Join();
        var batch = new StringBuilder();
        for (int i = 1; i <= 20; i++) batch.Append("{\"t\":\"cmd\",\"id\":").Append(i).Append(",\"verb\":\"show\"}\n");
        c.SendRaw(Encoding.UTF8.GetBytes(batch.ToString()));
        int ok = 0, limited = 0;
        for (int i = 0; i < 20; i++)
        {
            var ack = c.NextJson();
            if (ack.GetProperty("ok").GetBoolean()) ok++;
            else { Check.Equal("rateLimited", ack.GetProperty("error").GetString()); limited++; }
        }
        Check.Equal(5, ok, "exactly the burst of 5 went through");
        Check.Equal(15, limited, "the rest was limited");
        Check.True(Until(() => rig.Host.Commands.Length == ok));
    }

    [UnitTest]
    public static void Remote_the_command_limit_is_the_servers_so_a_reconnect_gets_no_new_burst()
    {
        // One command a second refills: a reconnect finds the bucket the first client emptied (the clock is moved by hand).
        var clock = new FakeClock();
        using var rig = new Rig(new RemoteServerOptions { CommandsPerSecond = 1, CommandBurst = 5, Clock = () => clock.Now }, Sample());
        int Burst(TestClient c, int from, int n)
        {
            var batch = new StringBuilder();
            for (int i = 0; i < n; i++) batch.Append("{\"t\":\"cmd\",\"id\":").Append(from + i).Append(",\"verb\":\"show\"}\n");
            c.SendRaw(Encoding.UTF8.GetBytes(batch.ToString()));
            int ok = 0;
            for (int i = 0; i < n; i++) if (c.NextJson().GetProperty("ok").GetBoolean()) ok++;
            return ok;
        }
        using (var first = rig.Connect())
        {
            first.Join();
            Check.Equal(5, Burst(first, 1, 10), "the first client used the burst");
        }
        Check.True(Until(() => rig.Server.ClientCount == 0), "the first left");
        using var second = rig.Connect();
        second.Join();
        Check.Equal(0, Burst(second, 100, 5), "a new connection does not get a new burst");
        clock.Advance(2);
        Check.Equal(2, Burst(second, 200, 5), "two seconds refill two commands");
    }

    [UnitTest]
    public static void Remote_a_connection_that_sends_too_many_lines_is_closed_before_and_after_hello()
    {
        using var rig = new Rig(first: Sample());
        var junk = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("x\n", 300)));
        using (var before = rig.Connect())
        {
            before.SendRaw(junk);   // no hello: junk lines count too
            Check.True(before.Closed(), "closed before its hello");
            Check.True(Until(() => rig.Server.ClientCount == 0));
            Check.Equal("flood", rig.Server.LastDropReason);
        }
        using var after = rig.Connect();
        after.Join();
        after.SendRaw(junk);   // (lines that get no answer, so its queue never fills first)
        Check.True(after.Closed(), "closed after its hello");
        Check.True(Until(() => rig.Server.ClientCount == 0));
        Check.Equal("flood", rig.Server.LastDropReason);
        Check.Equal(0, rig.Host.Commands.Length);
    }

    [UnitTest]
    public static void Remote_a_client_that_does_not_read_is_dropped()
    {
        using var rig = new Rig(new RemoteServerOptions { WriteTimeout = TimeSpan.FromMilliseconds(300) }, Sample());
        using var slow = rig.Connect(read: false);
        slow.Hello();
        Check.True(Until(() => rig.Server.ClientCount == 1));
        var sw = Stopwatch.StartNew();
        string line = RemoteProtocol.ClipSaved(1);
        while (rig.Server.ClientCount == 1 && sw.ElapsedMilliseconds < 15000)
        {
            rig.Server.PublishEvent(line);   // (the client only counts as ready once its hello was read; until then these are skipped)
            Thread.Yield();
        }
        Check.True(Until(() => rig.Server.ClientCount == 0), "the slow reader was dropped");
        Check.True(rig.Server.LastDropReason is "queueFull" or "writeTimeout", "reason: " + rig.Server.LastDropReason);
        // the server carries on: a new client is served
        using var good = rig.Connect();
        good.Join();
    }

    [UnitTest]
    public static void Remote_a_write_that_blocks_longer_than_the_timeout_drops_the_client()
    {
        // A queue far longer than the pipe's buffer, so however slow the machine, the writer blocks in a write before the queue is full.
        using var rig = new Rig(new RemoteServerOptions { WriteTimeout = TimeSpan.FromMilliseconds(150), MaxQueued = 100_000 }, Sample());
        using var slow = rig.Connect(read: false);
        slow.Hello();
        string line = RemoteProtocol.ClipSaved(1);
        var total = Stopwatch.StartNew();
        Check.True(Until(() => rig.Server.ClientCount == 1));
        while (rig.Server.ClientCount > 0 && total.ElapsedMilliseconds < 20000)
        {
            for (int i = 0; i < 50; i++) rig.Server.PublishEvent(line);   // (skipped until its hello was read)
            Until(() => rig.Server.ClientCount == 0, 50);
        }
        Check.True(Until(() => rig.Server.ClientCount == 0), "dropped");
        Check.Equal("writeTimeout", rig.Server.LastDropReason);
    }

    // ------------------------------------------------------------------ security

    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier MediumLabel = new("S-1-16-8192");
    private const int ReadWriteSync = (int)(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);
    private const int CreateInstance = (int)PipeAccessRights.CreateNewInstance;

    private static string UniqueName() => "Clink.Remote.dev.unittest-" + Guid.NewGuid().ToString("N") + ".s1";

    private static bool ProcessElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The DACL as (allow?, SID, mask), in order.</summary>
    private static List<(bool Allow, SecurityIdentifier Sid, int Mask)> Dacl(RawSecurityDescriptor sd) =>
        sd.DiscretionaryAcl!.Cast<GenericAce>().Select(a => (CommonAce)a).Select(a => (a.AceQualifier == AceQualifier.AccessAllowed, a.SecurityIdentifier, a.AccessMask)).ToList();

    /// <summary>The mandatory label entries as (SID, policy mask).</summary>
    private static List<(SecurityIdentifier Sid, int Mask)> Labels(RawSecurityDescriptor sd) =>
        (sd.SystemAcl?.Cast<GenericAce>() ?? Enumerable.Empty<GenericAce>()).Where(a => (byte)a.AceType == 0x11).Select(a =>
        {
            var b = new byte[a.BinaryLength];
            a.GetBinaryForm(b, 0);
            return (new SecurityIdentifier(b, 8), BitConverter.ToInt32(b, 4));   // header (4 bytes), mask, SID
        }).ToList();

    private static void CheckDescriptor(RawSecurityDescriptor sd, bool elevated, string what)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        Check.Equal(user, sd.Owner, what + ": owner is the user SID");
        var expected = new List<(bool, SecurityIdentifier, int)>
        {
            (false, Network, 0x1F01FF),          // NETWORK: nothing
            (true, OwnerRights, 0x20000),        // OWNER RIGHTS: READ_CONTROL only, so no implicit WRITE_DAC for the owner
        };
        if (elevated)
        {
            expected.Add((true, user, ReadWriteSync));                // the user connects, but adds no instance
            expected.Add((true, Administrators, CreateInstance));    // only the elevated Clink adds instances
        }
        else expected.Add((true, user, ReadWriteSync | CreateInstance));
        var dacl = Dacl(sd);
        Check.Equal(expected.Count, dacl.Count, what + ": entries");
        for (int i = 0; i < expected.Count; i++)
        {
            Check.Equal(expected[i].Item1, dacl[i].Allow, what + ": entry " + i + " allow");
            Check.Equal(expected[i].Item2, dacl[i].Sid, what + ": entry " + i + " SID");
            Check.Equal(expected[i].Item3, dacl[i].Mask, what + ": entry " + i + " mask");
        }
        var labels = Labels(sd);
        Check.Equal(1, labels.Count, what + ": one mandatory label");
        Check.Equal(MediumLabel, labels[0].Sid, what + ": Medium");
        Check.Equal(0x3, labels[0].Mask, what + ": no-write-up and no-read-up");
    }

    [UnitTest]
    public static void Remote_pipe_security_is_the_users_with_a_medium_label_in_both_modes()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        foreach (bool elevated in new[] { false, true })
        {
            string sddl = RemoteServer.PipeSddl(user, elevated);
            CheckDescriptor(new RawSecurityDescriptor(RemoteServer.SddlToBinary(sddl), 0), elevated, (elevated ? "elevated" : "normal") + " descriptor");
        }
    }

    [UnitTest]
    public static void Remote_an_instance_holds_exactly_the_descriptor_and_its_label()
    {
        string name = UniqueName();
        byte[] sd = RemoteServer.PipeSecurity(elevated: false);
        using var instance = RemoteServer.CreateInstance(name, sd, 4, first: true);
        var held = RemoteServer.ReadSecurity(instance.SafePipeHandle);   // what Windows really holds, label included
        CheckDescriptor(held, elevated: false, "instance");
        Check.Null(RemoteServer.SecurityDifference(new RawSecurityDescriptor(sd, 0), held), "the server's own check agrees");
        // instances 2 to 4 join it; a fifth is over the limit
        var more = new List<NamedPipeServerStream>();
        try
        {
            for (int i = 2; i <= 4; i++) more.Add(RemoteServer.CreateInstance(name, sd, 4, first: false));
            Check.Throws<Win32Exception>(() => RemoteServer.CreateInstance(name, sd, 4, first: false).Dispose(), "a fifth instance");
            // and with FIRST_PIPE_INSTANCE the existing pipe is never joined
            Check.Equal(5, Check.Throws<Win32Exception>(() => RemoteServer.CreateInstance(name, sd, 4, first: true).Dispose(), "first, while it exists").NativeErrorCode);
        }
        finally { foreach (var p in more) p.Dispose(); }
    }

    [UnitTest]
    public static void Remote_the_elevated_descriptor_lets_the_user_connect_but_not_add_an_instance()
    {
        // Made by this (not elevated) process: the first instance needs no CreateNewInstance right; the next one is checked against it.
        string name = UniqueName();
        var user = WindowsIdentity.GetCurrent().User!;
        byte[] sd = RemoteServer.SddlToBinary(RemoteServer.PipeSddl(user, elevated: true));
        using var instance = RemoteServer.CreateInstance(name, sd, 4, first: true);
        CheckDescriptor(RemoteServer.ReadSecurity(instance.SafePipeHandle), elevated: true, "elevated instance");
        var accepted = instance.WaitForConnectionAsync();
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            client.Connect(5000);   // read and write, as a Node or .NET client opens it
            Check.True(accepted.Wait(5000), "the instance accepted the user's client");
            client.Write(Encoding.ASCII.GetBytes("x\n"));
            var buf = new byte[2];
            Check.True(instance.ReadAsync(buf).AsTask().Wait(5000), "read");
            Check.Equal((byte)'x', buf[0]);
        }
        if (ProcessElevated())
        {
            using var second = RemoteServer.CreateInstance(name, sd, 4, first: false);   // an elevated run: Administrators may add instances
        }
        else
        {
            var refused = Check.Throws<Win32Exception>(() => RemoteServer.CreateInstance(name, sd, 4, first: false).Dispose(), "the user (not elevated) adds an instance");
            Check.Equal(5, refused.NativeErrorCode, "access denied");
        }
    }

    [UnitTest]
    public static void Remote_an_instance_of_a_pipe_someone_else_made_is_not_ours()
    {
        string name = UniqueName();
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        byte[] sd = RemoteServer.PipeSecurity(elevated: false);
        Check.NotNull(RemoteServer.SecurityDifference(new RawSecurityDescriptor(sd, 0), RemoteServer.ReadSecurity(squatter.SafePipeHandle)), "the default security is not ours");
        // joining it (not first: what the server never does while it has no instance) gets the squatter's security, which the check catches
        using var joined = RemoteServer.CreateInstance(name, sd, 4, first: false);
        Check.NotNull(RemoteServer.SecurityDifference(new RawSecurityDescriptor(sd, 0), RemoteServer.ReadSecurity(joined.SafePipeHandle)), "a joined instance has the squatter's");
    }

    [UnitTest]
    public static void Remote_an_own_first_instance_that_reads_back_differently_logs_the_difference_once()
    {
        // Created with the elevated descriptor while the server expects the normal one: a mismatch of our own making, not a squatter.
        var user = WindowsIdentity.GetCurrent().User!;
        byte[] other = RemoteServer.SddlToBinary(RemoteServer.PipeSddl(user, elevated: true));
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        int Said() { lock (lines) return lines.Count(l => l.Contains("remote: instance security differs: dacl", StringComparison.Ordinal)); }
        int Squatted() { lock (lines) return lines.Count(l => l.Contains("pipe name in use", StringComparison.Ordinal)); }
        Log.Line += Collect;
        try
        {
            using var rig = new Rig(new RemoteServerOptions { CreateSecurity = other, BackoffStart = TimeSpan.FromMilliseconds(10), BackoffMax = TimeSpan.FromMilliseconds(30) }, Sample());
            Check.True(Until(() => Said() == 1), "said");
            Thread.Sleep(300);   // several more tries at 10 to 30 ms
            Check.Equal(1, Said(), "once, not at every try");
            Check.Equal(0, Squatted(), "not reported as another process holding the name");
            Check.True(!rig.Server.NameInUse);
            Check.Equal(0, rig.Server.NameInUseRefusals);
        }
        finally { Log.Line -= Collect; }
    }

    [UnitTest]
    public static void Remote_without_a_session_check_the_real_one_runs_and_fails_closed()
    {
        string name = UniqueName();
        byte[] sd = RemoteServer.PipeSecurity(elevated: false);
        using var instance = RemoteServer.CreateInstance(name, sd, 4, first: true);
        var accepted = instance.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(5000);
        Check.True(accepted.Wait(5000), "accepted");
        uint ours = (uint)AppController.CurrentSessionId;
        Check.True(RemoteServer.SameSession(instance.SafePipeHandle, ours), "a client in our session");
        Check.True(!RemoteServer.SameSession(instance.SafePipeHandle, ours + 1), "a client in another session is refused");
        var gone = instance.SafePipeHandle;
        instance.Dispose();
        Check.True(!RemoteServer.SameSession(gone, ours), "a handle Windows cannot answer for is refused");
    }

    [UnitTest]
    public static void Remote_a_pipe_name_someone_else_holds_is_not_served_until_it_is_free()
    {
        string name = UniqueName();
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        int Said() { lock (lines) return lines.Count(l => l.Contains("pipe name in use (" + name + ")", StringComparison.Ordinal)); }
        var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var host = new FakeHost();
        var server = new RemoteServer(host, name, new RemoteServerOptions { BackoffStart = TimeSpan.FromMilliseconds(20), BackoffMax = TimeSpan.FromMilliseconds(100) });
        Log.Line += Collect;
        try
        {
            server.Start();
            Check.True(Until(() => server.NameInUseRefusals >= 4), "it keeps trying");
            Check.True(server.NameInUse);
            Check.Equal(0, server.ClientCount);
            Check.Equal(1, Said(), "said once, not at every try");
            squatter.Dispose();
            Check.True(Until(() => !server.NameInUse), "the name is free: it serves");
            using var c = new TestClient(name);
            c.Join(state: false);
            Check.Equal(1, server.ClientCount);
            Check.Equal(1, Said());
        }
        finally
        {
            Log.Line -= Collect;
            squatter.Dispose();
            server.Dispose();
        }
    }

    // ------------------------------------------------------------------ the key-file handshake

    private static byte[] KeyFrom(string path)
    {
        byte[] key = Convert.FromBase64String(ReadShared(path).Trim());
        Check.Equal(32, key.Length, "the key is 32 bytes");
        return key;
    }

    /// <summary>Reads as the plugin does (sharing read, write and delete), so a rename or refresh going on does not get in the way.</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs, Encoding.ASCII);
        return r.ReadToEnd();
    }

    /// <summary>The key in the file, or null while it is missing or cannot be read right now (for waits).</summary>
    private static byte[]? TryKeyFrom(string path)
    {
        try { return Convert.FromBase64String(ReadShared(path).Trim()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    private static string Nonce(int bytes = 32) => Convert.ToBase64String(Enumerable.Range(0, bytes).Select(i => (byte)(i * 7 + 3)).ToArray());

    [UnitTest]
    public static void Remote_the_welcome_carries_a_proof_only_for_a_valid_nonce()
    {
        using var rig = new Rig(first: Sample());
        byte[] key = KeyFrom(rig.WaitForKey());
        string nonce = Nonce(32);
        string expected = RemoteProtocol.Proof(key, rig.Name, Convert.FromBase64String(nonce));
        var bad = new Dictionary<string, string>
        {
            ["missing"] = "",
            ["not base64"] = ",\"nonce\":\"not base64!!\"",
            ["too short"] = ",\"nonce\":\"" + Nonce(15) + "\"",
            ["too long"] = ",\"nonce\":\"" + Nonce(65) + "\"",
            ["a number"] = ",\"nonce\":7",
        };
        foreach (var (what, field) in bad)
        {
            using var c = rig.Connect();
            c.Send("{\"t\":\"hello\",\"proto\":1,\"client\":\"unittest\"" + field + "}");
            var w = c.NextJson();
            Check.Equal("welcome", w.GetProperty("t").GetString(), what);
            Check.False(w.TryGetProperty("proof", out _), "no proof: " + what);
        }
        foreach (int n in new[] { 16, 32, 64 })
        {
            using var c = rig.Connect();
            string nn = Nonce(n);
            c.Send("{\"t\":\"hello\",\"proto\":1,\"nonce\":\"" + nn + "\"}");
            var w = c.NextJson();
            Check.Equal(RemoteProtocol.Proof(key, rig.Name, Convert.FromBase64String(nn)), w.GetProperty("proof").GetString(), "a " + n + " byte nonce");
        }
        using (var c = rig.Connect())
        {
            c.Send("{\"t\":\"hello\",\"proto\":1,\"nonce\":\"" + nonce + "\"}");
            Check.Equal(expected, c.NextJson().GetProperty("proof").GetString());
        }
        using var other = new Rig(first: Sample());   // another server, another key: its proof is not this one's
        using var oc = other.Connect();
        oc.Send("{\"t\":\"hello\",\"proto\":1,\"nonce\":\"" + nonce + "\"}");
        Check.True(oc.NextJson().GetProperty("proof").GetString() != expected, "a key per server");
        Check.True(!KeyFrom(other.WaitForKey()).SequenceEqual(key), "a key per server");
    }

    [UnitTest]
    public static void Remote_the_key_file_exists_before_the_first_welcome_and_goes_with_dispose()
    {
        var rig = new Rig(first: Sample());
        try
        {
            using var c = rig.Connect();
            c.Hello();
            Check.Equal("welcome", c.NextJson().GetProperty("t").GetString());
            Check.True(File.Exists(rig.KeyPath), "and when the welcome arrives");
            // The server alone, not the rig (whose Dispose removes the temp folder, file and all, so the check below could not fail).
            rig.Server.Dispose();
            Check.False(File.Exists(rig.KeyPath), "deleted by the server's Dispose");
            Check.True(Directory.Exists(rig.KeyDir), "the folder stays (only the file goes)");
            Check.Equal(0, Directory.GetFileSystemEntries(rig.KeyDir).Length, "nothing else left in the folder");
        }
        finally { rig.Dispose(); }
    }

    [UnitTest]
    public static void Remote_without_a_key_folder_no_file_is_written()
    {
        using var rig = new Rig(first: Sample(), keyFile: false);
        using var c = rig.Connect();
        c.Hello();
        var w = c.NextJson();
        Check.Equal("welcome", w.GetProperty("t").GetString());
        Check.False(Directory.Exists(rig.KeyDir), "no folder either");
    }

    [UnitTest]
    public static void Remote_the_key_file_has_the_owner_dacl_and_label_asked_for()
    {
        using var rig = new Rig(first: Sample());
        rig.WaitForKey();
        var user = WindowsIdentity.GetCurrent().User!;
        string sddl = "O:" + user.Value + "D:P(A;;FA;;;" + user.Value + ")S:(ML;;NWNR;;;ME)";   // written out here, not taken from the product
        var expected = new RawSecurityDescriptor(RemoteServer.SddlToBinary(sddl), 0);
        var actual = RemoteKeyFile.ReadSecurity(rig.KeyPath);
        Check.Null(RemoteServer.SecurityDifference(expected, actual), "owner, DACL and label read back exactly");
        Check.Equal(user, actual.Owner, "owner = the user SID");
        Check.True(actual.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), "protected: nothing inherited");
        Check.Equal(1, actual.DiscretionaryAcl!.Count, "one ACE");
        var ace = (CommonAce)actual.DiscretionaryAcl[0];
        Check.Equal(AceType.AccessAllowed, ace.AceType);
        Check.Equal(user, ace.SecurityIdentifier, "the user, nobody else");
        Check.Equal(0x1F01FF, ace.AccessMask, "full control");
        Check.Equal(1, actual.SystemAcl!.Cast<GenericAce>().Count(a => (byte)a.AceType == 0x11), "one mandatory label");
        // the folder was made by the server too: the same owner-only descriptor, a label, and it inherits to what is inside
        var dir = new DirectoryInfo(rig.KeyDir).GetAccessControl();
        Check.True(dir.AreAccessRulesProtected, "the folder is protected");
        Check.Equal(user, dir.GetOwner(typeof(SecurityIdentifier)), "the folder's owner");
        var rules = dir.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Check.Equal(1, rules.Length, "the folder has one rule");
        Check.Equal(user, rules[0].IdentityReference, "the user");
    }

    [UnitTest]
    public static void Remote_a_key_file_is_replaced_in_one_step_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        string folder = dir.File("remote");
        string pipe = "Clink.Remote.dev.unittest";
        string path = RemoteKeyFile.PathFor(folder, pipe);
        byte[] a = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), b = Enumerable.Range(0, 32).Select(i => (byte)(i + 100)).ToArray();
        RemoteKeyFile.Write(folder, pipe, a);
        Check.Equal(Convert.ToBase64String(a), File.ReadAllText(path));
        RemoteKeyFile.Write(folder, pipe, b);   // over an existing file (a stale one of a crashed run)
        Check.Equal(Convert.ToBase64String(b), File.ReadAllText(path));
        Check.Equal(new[] { path }, Directory.GetFileSystemEntries(folder), "only the key file");
        Check.Null(RemoteServer.SecurityDifference(new RawSecurityDescriptor(RemoteServer.SddlToBinary(RemoteKeyFile.FileSddl(WindowsIdentity.GetCurrent().User!)), 0), RemoteKeyFile.ReadSecurity(path)), "the replacement has the security too");
        // a move that cannot happen (the name is a folder) throws and still leaves no temp file
        string blocked = RemoteKeyFile.PathFor(folder, "Clink.Remote.dev.blocked");
        Directory.CreateDirectory(blocked);
        Check.Throws<Win32Exception>(() => RemoteKeyFile.Write(folder, "Clink.Remote.dev.blocked", a), "moving over a folder");
        Check.Equal(0, Directory.GetFiles(folder, "*.tmp").Length, "no temp file");
        Check.False(RemoteKeyFile.Delete(folder, pipe, a), "another key than the one it holds: not deleted");
        Check.Equal(Convert.ToBase64String(b), File.ReadAllText(path), "still there");
        Check.True(RemoteKeyFile.Delete(folder, pipe, b), "its own key: deleted");
        Check.False(File.Exists(path));
        Check.False(RemoteKeyFile.Delete(folder, pipe, b), "nothing there: nothing to say");
        Check.Throws<ArgumentException>(() => RemoteKeyFile.Write(folder, "..\\evil", a), "a pipe name is a file name");
    }

    [UnitTest]
    public static void Remote_the_key_file_is_written_when_a_squatter_lets_go_of_the_name_and_goes_with_dispose()
    {
        string name = UniqueName();
        using var dir = new TempDir();
        string folder = dir.File("remote");
        string path = RemoteKeyFile.PathFor(folder, name);
        var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var server = new RemoteServer(new FakeHost(), name, new RemoteServerOptions { KeyDir = folder, BackoffStart = TimeSpan.FromMilliseconds(20), BackoffMax = TimeSpan.FromMilliseconds(100) });
        try
        {
            server.Start();
            Check.True(Until(() => server.NameInUseRefusals >= 3), "it keeps trying");
            Check.False(File.Exists(path), "no key file for a pipe we do not own");
            squatter.Dispose();
            Check.True(Until(() => File.Exists(path)), "the name is free: the key is published");
            Check.True(Until(() => !server.NameInUse));
            using var c = new TestClient(name);
            c.Join(state: false);
            Check.True(File.Exists(path));
        }
        finally
        {
            squatter.Dispose();
            server.Dispose();
        }
        Check.False(File.Exists(path), "deleted by Dispose");
    }

    [UnitTest]
    public static void Remote_a_server_that_cannot_write_its_key_file_still_serves_and_says_so_once()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("blocker"), "a file where the folder would go");
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        int Said() { lock (lines) return lines.Count(l => l.Contains("remote: key file not written", StringComparison.Ordinal)); }
        int Recovered() { lock (lines) return lines.Count(l => l.Contains("remote: key file written", StringComparison.Ordinal)); }
        Log.Line += Collect;
        string name = UniqueName();
        string folder = dir.File("blocker\\sub");
        var server = new RemoteServer(new FakeHost(), name, new RemoteServerOptions { KeyDir = folder });
        try
        {
            server.Start();
            for (int i = 0; i < 3; i++)
            {
                using var c = new TestClient(name);
                c.Join(state: false);   // served all the same, each time (every connect is another try to write)
            }
            Check.Equal(1, Said(), "one warning, not one per try");
            Check.Equal(0, Recovered());
            // the blocker goes: the next try (after the next connect) writes the file, and says so once
            File.Delete(dir.File("blocker"));
            using (var c = new TestClient(name)) c.Join(state: false);
            Check.True(Until(() => File.Exists(RemoteKeyFile.PathFor(folder, name))), "written once the folder can be made");
            Check.True(Until(() => Recovered() == 1), "the recovery is said");
            using (var c = new TestClient(name)) c.Join(state: false);
            Check.Equal(1, Recovered(), "once");
            Check.Equal(1, Said());
        }
        finally
        {
            Log.Line -= Collect;
            server.Dispose();
        }
    }

    [UnitTest]
    public static void Remote_a_server_that_never_got_the_name_leaves_the_owners_key_file_alone()
    {
        string name = UniqueName();
        using var dir = new TempDir();
        string folder = dir.File("remote");
        string path = RemoteKeyFile.PathFor(folder, name);
        var owner = new RemoteServer(new FakeHost(), name, new RemoteServerOptions { KeyDir = folder });
        RemoteServer? other = null;
        try
        {
            owner.Start();
            Check.True(Until(() => File.Exists(path)), "the owner published its key");
            byte[] key = KeyFrom(path);
            other = new RemoteServer(new FakeHost(), name, new RemoteServerOptions { KeyDir = folder, BackoffStart = TimeSpan.FromMilliseconds(20), BackoffMax = TimeSpan.FromMilliseconds(100) });
            other.Start();
            Check.True(Until(() => other.NameInUseRefusals >= 3), "the other server keeps finding the name taken");
            Check.True(KeyFrom(path).SequenceEqual(key), "its refusals left the owner's file alone");
            other.Dispose();
            Check.True(KeyFrom(path).SequenceEqual(key), "and so did its Dispose");
            using var c = new TestClient(name);   // the owner still serves, with the key in the file
            string nonce = Nonce();
            c.Send("{\"t\":\"hello\",\"proto\":1,\"nonce\":\"" + nonce + "\"}");
            Check.Equal(RemoteProtocol.Proof(key, name, Convert.FromBase64String(nonce)), c.NextJson().GetProperty("proof").GetString());
        }
        finally
        {
            other?.Dispose();
            owner.Dispose();
        }
        Check.False(File.Exists(path), "the owner's Dispose deletes its own");
    }

    [UnitTest]
    public static void Remote_a_server_that_lost_the_name_never_deletes_the_new_owners_key_file()
    {
        // An update's handover: the old Clink published, lost the name, and the new one wrote its own key over the file.
        var rig = new Rig(first: Sample());
        try
        {
            byte[] old = KeyFrom(rig.WaitForKey());
            byte[] fresh = RandomNumberGenerator.GetBytes(32);
            RemoteKeyFile.Write(rig.KeyDir, rig.Name, fresh);   // what the new owner's server does
            Check.True(!old.SequenceEqual(fresh));
            rig.Server.NameTakenForTests();
            Check.True(rig.Server.NameInUse);
            Check.True(KeyFrom(rig.KeyPath).SequenceEqual(fresh), "finding the name taken does not delete the new owner's file");
            rig.Server.Dispose();
            Check.True(KeyFrom(rig.KeyPath).SequenceEqual(fresh), "nor does Dispose");
        }
        finally { rig.Dispose(); }
    }

    [UnitTest]
    public static void Remote_the_session_ending_stops_the_pipe_and_deletes_the_key_file()
    {
        using var dir = new TempDir();
        Settings.DirOverride = dir.Path;
        var settings = Settings.FromJson("{}");
        AppController.InstanceSuffix = ".dev.unittest-" + Guid.NewGuid().ToString("N");
        string name = AppController.RemotePipeName;
        string folder = dir.File("remote");
        string path = RemoteKeyFile.PathFor(folder, name);
        using var app = new AppController(settings, new Updater(settings));
        app.StartRemoteForTests(folder);
        Check.True(Until(() => File.Exists(path)), "published");
        using var c = new TestClient(name);
        c.Join(state: false);
        app.OnSessionEnding();   // what App's SessionEnding runs
        Check.False(File.Exists(path), "the key file went with the session end, before OnExit");
        Check.True(c.Closed(), "the client was closed");
        Check.Throws<TimeoutException>(() => { using var late = new TestClient(name, connectMs: 300); }, "nothing accepts any more");
    }

    [UnitTest]
    public static void Remote_the_key_folder_must_be_the_users_and_only_the_user_SYSTEM_and_Administrators_may_write_to_it()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        string u = user.Value;
        string? Problem(string sddl) => RemoteKeyFile.FolderProblem(new RawSecurityDescriptor(RemoteServer.SddlToBinary(sddl), 0), user);
        Check.Null(Problem(RemoteKeyFile.DirSddl(user)), "the folder Clink makes");
        Check.Null(Problem("O:" + u + "D:P(A;OICI;FA;;;" + u + ")(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"), "the user, SYSTEM and Administrators (what a profile folder inherits)");
        Check.Null(Problem("O:" + u + "D:P(A;;FA;;;" + u + ")(A;;FR;;;WD)"), "others may read");
        Check.Null(Problem("O:" + u + "D:P(A;;FA;;;" + u + ")(D;;FA;;;WD)"), "a deny entry only takes rights away");
        Check.Null(Problem("O:" + u + "D:P(A;;FA;;;" + u + ")S:(ML;;NW;;;HI)"), "a High label");
        Check.Equal("owner", Problem("O:BAD:P(A;;FA;;;" + u + ")"), "someone else's");
        Check.Equal("others can write", Problem("O:" + u + "D:P(A;;FA;;;" + u + ")(A;OICI;0x1301bf;;;WD)"), "Everyone: modify");
        Check.Equal("others can write", Problem("O:" + u + "D:P(A;;FA;;;" + u + ")(A;;GW;;;AU)"), "Authenticated Users: generic write");
        Check.Equal("others can write", Problem("O:" + u + "D:P(A;;FA;;;" + u + ")(A;;0x40;;;BU)"), "Users: delete child alone");
        Check.Equal("no dacl", Problem("O:" + u + "D:NO_ACCESS_CONTROL"), "a null DACL lets everyone in");
        Check.Equal("label", Problem("O:" + u + "D:P(A;;FA;;;" + u + ")S:(ML;;NW;;;LW)"), "a Low label lets the user's Low processes write");
    }

    [UnitTest]
    public static void Remote_a_key_folder_of_ours_that_others_can_write_to_gets_our_security_again()
    {
        using var dir = new TempDir();
        var user = WindowsIdentity.GetCurrent().User!;
        string folder = dir.File("remote");
        var weak = new DirectorySecurity();
        weak.SetSecurityDescriptorSddlForm("O:" + user.Value + "D:P(A;OICI;FA;;;" + user.Value + ")(A;OICI;0x1301bf;;;WD)");   // ours, Everyone: modify
        new DirectoryInfo(folder).Create(weak);
        RawSecurityDescriptor Held() => new(new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), 0);
        Check.Equal("others can write", RemoteKeyFile.FolderProblem(Held(), user), "made weak");
        string pipe = "Clink.Remote.dev.unittest";
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)(i + 7)).ToArray();
        RemoteKeyFile.Write(folder, pipe, key);
        Check.Equal(Convert.ToBase64String(key), File.ReadAllText(RemoteKeyFile.PathFor(folder, pipe)), "published");
        Check.Null(RemoteKeyFile.FolderProblem(Held(), user), "only the user may write now");
        var acl = new DirectoryInfo(folder).GetAccessControl();
        Check.True(acl.AreAccessRulesProtected, "protected");
        Check.Equal(user, acl.GetOwner(typeof(SecurityIdentifier)));
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Check.Equal(1, rules.Length, "Everyone is gone");
        Check.Equal(user, rules[0].IdentityReference);
    }

    [UnitTest]
    public static void Remote_a_key_folder_that_is_a_junction_is_refused_and_said_once()
    {
        using var dir = new TempDir();
        string target = dir.File("elsewhere");
        Directory.CreateDirectory(target);
        string link = dir.File("remote");
        if (!TestJunction.TryMake(link, target, out string why)) TestJunction.Skipped(nameof(Remote_a_key_folder_that_is_a_junction_is_refused_and_said_once), why);
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        int Said() { lock (lines) return lines.Count(l => l.Contains("remote: key folder not trusted: a link or other reparse point", StringComparison.Ordinal)); }
        string name = UniqueName();
        RemoteServer? server = null;
        Log.Line += Collect;
        try
        {
            var refused = Check.Throws<RemoteKeyFolderUntrustedException>(() => RemoteKeyFile.Write(link, name, new byte[32]), "a junction");
            Check.Contains(refused.Message, "key folder not trusted");
            Check.Equal(0, Directory.GetFileSystemEntries(target).Length, "nothing was written through it");
            server = new RemoteServer(new FakeHost(), name, new RemoteServerOptions { KeyDir = link });
            server.Start();
            for (int i = 0; i < 3; i++)
            {
                using var c = new TestClient(name);
                c.Join(state: false);   // served all the same
            }
            Check.True(Until(() => Said() == 1), "said");
            Check.Equal(1, Said(), "once, not at every try");
            Check.Equal(0, Directory.GetFileSystemEntries(target).Length, "the server wrote nothing through it either");
        }
        finally
        {
            Log.Line -= Collect;
            server?.Dispose();
            TestJunction.Remove(link);
        }
    }

    [UnitTest]
    public static void Remote_a_write_sweeps_the_temp_files_a_crash_left_and_nothing_else()
    {
        using var dir = new TempDir();
        string folder = dir.File("remote");
        string pipe = "Clink.Remote.dev.unittest";
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
        RemoteKeyFile.Write(folder, pipe, key);   // makes the folder
        string path = RemoteKeyFile.PathFor(folder, pipe);
        string stale = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string[] kept = { path + ".notes.tmp", RemoteKeyFile.PathFor(folder, "Clink.Remote.dev.other") + "." + Guid.NewGuid().ToString("N") + ".tmp", Path.Combine(folder, "readme.txt") };
        File.WriteAllText(stale, "a crashed run's");
        foreach (string k in kept) File.WriteAllText(k, "not a temp file of this pipe's");
        RemoteKeyFile.Write(folder, pipe, key);
        Check.False(File.Exists(stale), "the crashed run's temp file is gone");
        foreach (string k in kept) Check.True(File.Exists(k), "kept: " + Path.GetFileName(k));
        Check.Equal(Convert.ToBase64String(key), File.ReadAllText(path));
    }

    [UnitTest]
    public static void Remote_the_key_file_never_goes_to_the_real_local_folder_in_a_test()
    {
        string real = RemoteKeyFile.DefaultDir;
        Check.True(TestGuard.IsUnderTrueRealDir(real, TestGuard.TrueLocalDir), "the product's folder is the one the guard protects");
        string name = "Clink.Remote.dev.guardtest-" + Guid.NewGuid().ToString("N");
        Check.Throws<InvalidOperationException>(() => RemoteKeyFile.Write(real, name, new byte[32]), "refused");
        Check.Throws<InvalidOperationException>(() => RemoteKeyFile.Delete(real, name, new byte[32]), "refused");
        Check.Equal(2, TestGuard.TakeViolations().Length, "this test provoked exactly these two refusals");
        Check.False(File.Exists(RemoteKeyFile.PathFor(real, name)), "nothing was written");
    }

    // ------------------------------------------------------------------ the key file's upkeep

    [UnitTest]
    public static void Remote_the_key_upkeep_writes_the_file_once_a_blocker_goes_with_no_client_connecting()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("blocker"), "a file where the folder would go");
        var lines = new List<string>();
        void Collect(string line) { lock (lines) lines.Add(line); }
        int Said() { lock (lines) return lines.Count(l => l.Contains("remote: key file not written", StringComparison.Ordinal)); }
        int Recovered() { lock (lines) return lines.Count(l => l.Contains("remote: key file written", StringComparison.Ordinal)); }
        Log.Line += Collect;
        string name = UniqueName();
        string folder = dir.File("blocker\\sub");
        string path = RemoteKeyFile.PathFor(folder, name);
        var server = new RemoteServer(new FakeHost(), name, new RemoteServerOptions
        {
            KeyDir = folder, KeyRetryStart = TimeSpan.FromMilliseconds(20), KeyRetryMax = TimeSpan.FromMilliseconds(80),
        });
        try
        {
            server.Start();
            Check.True(Until(() => Said() == 1), "the failure is said");
            Check.True(Until(() => server.KeyUpkeepRuns >= 3), "the upkeep keeps trying, with no client");
            Check.Equal(1, Said(), "once, not at every try");
            File.Delete(dir.File("blocker"));
            Check.True(Until(() => File.Exists(path)), "written once the folder can be made, with no client connecting");
            Check.True(Until(() => Recovered() == 1), "the recovery is said");
            // the key in the file is the one the welcome proves with
            using var c = new TestClient(name);
            string nonce = Nonce();
            c.Send("{\"t\":\"hello\",\"proto\":1,\"nonce\":\"" + nonce + "\"}");
            Check.Equal(RemoteProtocol.Proof(KeyFrom(path), name, Convert.FromBase64String(nonce)), c.NextJson().GetProperty("proof").GetString());
        }
        finally
        {
            Log.Line -= Collect;
            server.Dispose();
        }
        Check.False(File.Exists(path), "deleted by Dispose");
    }

    [UnitTest]
    public static void Remote_a_key_file_deleted_or_changed_from_outside_is_written_again()
    {
        using var rig = new Rig(new RemoteServerOptions
        {
            KeyRefresh = TimeSpan.FromMilliseconds(50), KeyRetryStart = TimeSpan.FromMilliseconds(20), KeyRetryMax = TimeSpan.FromMilliseconds(50),
        }, Sample());
        string path = rig.WaitForKey();
        byte[] key = KeyFrom(path);
        File.Delete(path);
        Check.True(Until(() => TryKeyFrom(path) is { } k && k.SequenceEqual(key)), "deleted from outside: it reappears, with the same key");
        RemoteKeyFile.Write(rig.KeyDir, rig.Name, RandomNumberGenerator.GetBytes(32));   // something else in its place
        Check.True(Until(() => TryKeyFrom(path) is { } k && k.SequenceEqual(key)), "changed from outside: our key again");
        using var c = rig.Connect();
        string nonce = Nonce();
        c.Send("{\"t\":\"hello\",\"proto\":1,\"nonce\":\"" + nonce + "\"}");
        Check.Equal(RemoteProtocol.Proof(key, rig.Name, Convert.FromBase64String(nonce)), c.NextJson().GetProperty("proof").GetString(), "the server's key did not change");
    }

    [UnitTest]
    public static void Remote_the_key_upkeep_touches_the_file_every_interval_and_at_once_on_RefreshKeyNow()
    {
        // The contract with streamdeck/src/handshake.ts: the plugin calls a key file stale after StaleAfter without a touch.
        Check.Equal(60.0, RemoteKeyFile.RefreshInterval.TotalSeconds, "refresh interval (contract)");
        Check.Equal(300.0, RemoteKeyFile.StaleAfter.TotalSeconds, "staleness limit (contract)");
        Check.True(RemoteKeyFile.StaleAfter >= 2 * RemoteKeyFile.RefreshInterval + RemoteKeyFile.RetryMax, "a missed refresh and a retry never make it stale");
        DateTime old = DateTime.UtcNow - TimeSpan.FromHours(1);
        bool Fresh(string p) { try { return File.GetLastWriteTimeUtc(p) > DateTime.UtcNow - TimeSpan.FromMinutes(5); } catch (IOException) { return false; } }
        using (var rig = new Rig(new RemoteServerOptions { KeyRefresh = TimeSpan.FromMilliseconds(100) }, Sample()))
        {
            string path = rig.WaitForKey();
            byte[] key = KeyFrom(path);
            File.SetLastWriteTimeUtc(path, old);
            Check.False(Fresh(path), "made old");
            Check.True(Until(() => Fresh(path)), "touched on the interval");
            Check.True(KeyFrom(path).SequenceEqual(key), "touched, the key is the same");
        }
        using (var rig = new Rig(new RemoteServerOptions { KeyRefresh = TimeSpan.FromHours(1) }, Sample()))
        {
            string path = rig.WaitForKey();
            File.SetLastWriteTimeUtc(path, old);
            rig.Server.RefreshKeyNow();   // what a resume from sleep does
            Check.True(Until(() => Fresh(path)), "touched at once, not an interval later");
        }
    }

    [UnitTest]
    public static void Remote_no_key_upkeep_runs_after_dispose()
    {
        var rig = new Rig(new RemoteServerOptions
        {
            KeyRefresh = TimeSpan.FromMilliseconds(20), KeyRetryStart = TimeSpan.FromMilliseconds(10), KeyRetryMax = TimeSpan.FromMilliseconds(20),
        }, Sample());
        try
        {
            string path = rig.WaitForKey();
            Check.True(Until(() => rig.Server.KeyUpkeepRuns >= 2), "the upkeep runs");
            rig.Server.Dispose();
            Check.False(File.Exists(path), "deleted by Dispose");
            Check.True(Until(() => rig.Server.KeyUpkeepEnded), "the upkeep ended with Dispose");
            int runs = rig.Server.KeyUpkeepRuns;
            rig.Server.RefreshKeyNow();   // a resume after Dispose: nothing is there to do it
            Check.Equal(runs, rig.Server.KeyUpkeepRuns, "no pass after it ended");
            Check.False(File.Exists(path), "not written again");
            Check.Equal(0, Directory.GetFileSystemEntries(rig.KeyDir).Length, "nothing left in the folder");
        }
        finally { rig.Dispose(); }
    }

    [UnitTest]
    public static void Remote_WithdrawKey_deletes_the_key_at_once_and_it_is_never_published_again()
    {
        using var rig = new Rig(new RemoteServerOptions
        {
            KeyRefresh = TimeSpan.FromMilliseconds(20), KeyRetryStart = TimeSpan.FromMilliseconds(10), KeyRetryMax = TimeSpan.FromMilliseconds(20),
        }, Sample());
        string path = rig.WaitForKey();
        rig.Server.WithdrawKey();   // what the session end does first
        Check.False(File.Exists(path), "gone at once, while the pipe still runs");
        for (int i = 0; i < 3; i++)
        {
            using var c = rig.Connect();
            c.Join();   // still served: every accept would publish again, and does not
        }
        rig.Server.RefreshKeyNow();
        using (var c = rig.Connect()) c.Join();
        Check.False(File.Exists(path), "never published again");
        rig.Server.WithdrawKey();   // twice is fine
    }

    // ------------------------------------------------------------------ the key folder's parents (elevated Clink)

    private static readonly byte[] SomeKey = Enumerable.Range(0, 32).Select(i => (byte)(i * 5 + 1)).ToArray();

    private static SecurityIdentifier? RealOwner(string folder) =>
        new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    [UnitTest]
    public static void Remote_elevated_the_key_folder_is_only_made_inside_a_folder_the_user_owns()
    {
        using var dir = new TempDir();
        var user = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var someoneElses = new RemoteKeyFileHooks { Owner = _ => system };
        const string pipe = "Clink.Remote.dev.unittest";
        // the parent (Clink) is there and someone else's: remote is not made
        string parent = dir.File("Clink");
        Directory.CreateDirectory(parent);
        string folder = Path.Combine(parent, "remote");
        var refused = Check.Throws<RemoteKeyFolderUntrustedException>(() => RemoteKeyFile.Write(folder, pipe, SomeKey, elevated: true, hooks: someoneElses), "a parent that is not the user's");
        Check.Contains(refused.Message, "the folder above it is not the user's");
        Check.False(Directory.Exists(folder), "remote was not made");
        Check.Equal(0, Directory.GetFileSystemEntries(parent).Length, "nothing at all was made in it");
        // the parent is missing and the folder above it is someone else's: neither is made
        string missing = dir.File("Clink2");
        string folder2 = Path.Combine(missing, "remote");
        Check.Throws<RemoteKeyFolderUntrustedException>(() => RemoteKeyFile.Write(folder2, pipe, SomeKey, elevated: true, hooks: someoneElses), "the folder above the missing parent is not the user's");
        Check.False(Directory.Exists(missing), "the parent was not made");
        // not elevated, the user's own token makes them (nothing it could not make anyway): the seam is not asked
        string folder3 = Path.Combine(dir.File("Clink3"), "remote");
        RemoteKeyFile.Write(folder3, pipe, SomeKey, elevated: false, hooks: someoneElses);
        Check.True(File.Exists(RemoteKeyFile.PathFor(folder3, pipe)), "not elevated: written");
        // the user's own folders pass, and a missing parent is made with the user as owner
        if (RealOwner(dir.Path) != user) { Log.Warn("[unittest] the temp folder is not the user's: the positive elevated case is not checked here"); return; }
        RemoteKeyFile.Write(folder, pipe, SomeKey, elevated: true);
        Check.Equal(Convert.ToBase64String(SomeKey), ReadShared(RemoteKeyFile.PathFor(folder, pipe)), "a user-owned parent: written");
        RemoteKeyFile.Write(folder2, pipe, SomeKey, elevated: true);
        Check.Equal(Convert.ToBase64String(SomeKey), ReadShared(RemoteKeyFile.PathFor(folder2, pipe)), "a missing parent in a user-owned folder: made, and written");
        Check.Equal(user, RealOwner(missing), "the parent made elevated is the user's");
        Check.Equal(user, RealOwner(folder2), "and so is remote");
    }

    [UnitTest]
    public static void Remote_elevated_a_parent_junction_is_judged_where_it_points()
    {
        using var dir = new TempDir();
        var user = WindowsIdentity.GetCurrent().User!;
        const string pipe = "Clink.Remote.dev.unittest";
        // a moved AppData: the parent is a junction to a folder of the user's elsewhere
        string moved = dir.File("moved");
        Directory.CreateDirectory(moved);
        string link = dir.File("Clink");
        if (!TestJunction.TryMake(link, moved, out string why)) TestJunction.Skipped(nameof(Remote_elevated_a_parent_junction_is_judged_where_it_points), why);
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string sysLink = dir.File("ClinkSys");
        try
        {
            if (RealOwner(moved) == user)
            {
                RemoteKeyFile.Write(Path.Combine(link, "remote"), pipe, SomeKey, elevated: true);
                Check.Equal(Convert.ToBase64String(SomeKey), ReadShared(RemoteKeyFile.PathFor(Path.Combine(moved, "remote"), pipe)), "a moved, user-owned parent passes");
                Check.True(RemoteKeyFile.Delete(Path.Combine(link, "remote"), pipe, SomeKey), "and the file is deleted through it");
            }
            else Log.Warn("[unittest] the temp folder is not the user's: the moved-AppData case is not checked here");
            // a junction to a system folder: judged where it points (not the user's), and nothing is made there. Only when not elevated,
            // so that even a broken check could not make a folder in it.
            if (ProcessElevated()) return;
            Check.True(TestJunction.TryMake(sysLink, system32, out string why2), "a junction to the system folder: " + why2);
            var refused = Check.Throws<RemoteKeyFolderUntrustedException>(() => RemoteKeyFile.Write(Path.Combine(sysLink, "remote"), pipe, SomeKey, elevated: true), "a parent that points to a system folder");
            Check.Contains(refused.Message, "the folder above it is not the user's");
            Check.False(Directory.Exists(Path.Combine(system32, "remote")), "nothing was made there");
        }
        finally
        {
            TestJunction.Remove(link);
            TestJunction.Remove(sysLink);
        }
    }

    [UnitTest]
    public static void Remote_a_parent_re_pointed_after_the_check_does_not_divert_the_write()
    {
        using var dir = new TempDir();
        const string pipe = "Clink.Remote.dev.unittest";
        string a = dir.File("a"), b = dir.File("b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(Path.Combine(b, "remote"));
        string link = dir.File("Clink");
        if (!TestJunction.TryMake(link, a, out string why)) TestJunction.Skipped(nameof(Remote_a_parent_re_pointed_after_the_check_does_not_divert_the_write), why);
        try
        {
            string folder = Path.Combine(link, "remote");
            RemoteKeyFile.Write(folder, pipe, SomeKey);   // makes a\remote
            byte[] next = RandomNumberGenerator.GetBytes(32);
            bool repointed = false;
            var hooks = new RemoteKeyFileHooks { AfterCheck = () => { TestJunction.Remove(link); repointed = TestJunction.TryMake(link, b, out _); } };
            RemoteKeyFile.Write(folder, pipe, next, hooks: hooks);
            Check.True(repointed, "the parent junction was re-pointed between the check and the write");
            Check.Equal(Convert.ToBase64String(next), ReadShared(RemoteKeyFile.PathFor(Path.Combine(a, "remote"), pipe)), "written into the folder that was checked");
            Check.Equal(0, Directory.GetFileSystemEntries(Path.Combine(b, "remote")).Length, "nothing went where the link points now");
            Check.Equal(1, Directory.GetFileSystemEntries(Path.Combine(a, "remote")).Length, "no temp file left");
        }
        finally { TestJunction.Remove(link); }
    }

    [UnitTest]
    public static void Remote_a_temp_file_that_lands_outside_the_key_folder_is_refused_and_removed()
    {
        using var dir = new TempDir();
        const string pipe = "Clink.Remote.dev.unittest";
        string folder = dir.File("remote"), elsewhere = dir.File("elsewhere");
        Directory.CreateDirectory(elsewhere);
        RemoteKeyFile.Write(folder, pipe, SomeKey);   // an older file of ours, to see it is not replaced
        var hooks = new RemoteKeyFileHooks { TempPath = p => Path.Combine(elsewhere, Path.GetFileName(p)) };   // as a link swapped in at the last moment would
        var refused = Check.Throws<RemoteKeyFolderUntrustedException>(() => RemoteKeyFile.Write(folder, pipe, RandomNumberGenerator.GetBytes(32), hooks: hooks), "a temp file outside the folder");
        Check.Contains(refused.Message, "did not land in the key folder");
        Check.Equal(0, Directory.GetFileSystemEntries(elsewhere).Length, "the stray file was removed");
        Check.Equal(Convert.ToBase64String(SomeKey), ReadShared(RemoteKeyFile.PathFor(folder, pipe)), "the file in place is untouched");
        Check.Equal(1, Directory.GetFileSystemEntries(folder).Length, "nothing else in the folder");
    }
}
