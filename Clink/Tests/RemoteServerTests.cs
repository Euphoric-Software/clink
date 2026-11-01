using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
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

        public Rig(RemoteServerOptions? options = null, RemoteSnapshot? first = null)
        {
            AppController.InstanceSuffix = ".dev.unittest-" + Guid.NewGuid().ToString("N");
            Name = AppController.RemotePipeName;
            Server = new RemoteServer(Host, Name, options);
            if (first != null) Server.Publish(first);
            Server.Start();
        }

        public TestClient Connect(bool read = true) => new(Name, read);
        public void Dispose() => Server.Dispose();
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
}
