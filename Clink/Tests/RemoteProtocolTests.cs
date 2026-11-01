using System.Text.Json;
using System.Text.RegularExpressions;
using Clink.Services;

namespace Clink.Tests;

/// <summary>The remote status pipe's wire format: parsing, the verb allow-list, the serialised messages (no I/O).</summary>
internal static class RemoteProtocolTests
{
    internal static RemoteSnapshot Sample(string state = "recording", int s = 12) =>
        new(state, new RemoteRec(true, s, false, false, false), 30, false, true, true, true, new RemoteUpload(false, 0), false, false);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [UnitTest]
    public static void Remote_a_line_is_a_message_only_when_it_is_a_json_object_with_a_string_type()
    {
        Check.Null(RemoteProtocol.Parse("not json"));
        Check.Null(RemoteProtocol.Parse("[1,2]"));
        Check.Null(RemoteProtocol.Parse("{}"));
        Check.Null(RemoteProtocol.Parse("{\"t\":5}"));
        Check.Null(RemoteProtocol.Parse(""));
        var m = RemoteProtocol.Parse("{\"t\":\"cmd\",\"id\":7,\"verb\":\"clip\",\"extra\":{\"a\":[1,2]}}")!;
        Check.Equal("cmd", m.Type);
        Check.Equal(7L, m.Id);
        Check.Equal("clip", m.Verb);
        var h = RemoteProtocol.Parse("{\"t\":\"hello\",\"proto\":1,\"client\":\"deck\",\"v\":\"0.1\"}")!;
        Check.Equal(1, h.Proto);
        Check.Equal("deck", h.Client);
        Check.Null(RemoteProtocol.Parse("{\"t\":\"hello\",\"proto\":\"1\"}")!.Proto, "a proto that is not a number is no proto");
        Check.Null(RemoteProtocol.Parse("{\"t\":\"cmd\",\"id\":\"x\"}")!.Id);
        Check.Equal("zzz", RemoteProtocol.Parse("{\"t\":\"zzz\"}")!.Type, "an unknown type parses; the server ignores it");
    }

    [UnitTest]
    public static void Remote_only_the_seven_verbs_map_to_commands_and_the_edit_verbs_are_not_allowed()
    {
        foreach (string v in new[] { "clip", "record", "pause", "resume", "screenshot", "share", "show" })
        {
            Check.Null(RemoteProtocol.Resolve(v, out string cmd), v);
            Check.Equal(v, cmd);
        }
        Check.Equal(new[] { "clip", "record", "pause", "resume", "screenshot", "share", "show" }, RemoteProtocol.Verbs.ToArray(), "the welcome's list, from the same table");
        foreach (string v in new[] { "clip", "record", "pause", "resume", "share" }) Check.True(RemoteProtocol.NeedsClips(v), v + " needs Clips");
        foreach (string v in new[] { "screenshot", "show", "format", "" }) Check.False(RemoteProtocol.NeedsClips(v), v + " does not");
        foreach (string v in new[] { "screenshot-edit", "screenshot-edit-last", "trim", "Trim", "screenshot-edit C:\\x.png" })
        {
            Check.Equal("notAllowed", RemoteProtocol.Resolve(v, out string cmd), v);
            Check.Equal("", cmd, "nothing to run");
        }
        foreach (string? v in new[] { "", "CLIP", "clip ", "quit", "clip; show", null })
        {
            Check.Equal("unknownVerb", RemoteProtocol.Resolve(v, out string cmd), v ?? "null");
            Check.Equal("", cmd);
        }
    }

    [UnitTest]
    public static void Remote_the_state_message_carries_every_field()
    {
        var snap = new RemoteSnapshot("waitingForGame", new RemoteRec(true, 65, true, true, true), 120, true, false, true, false, new RemoteUpload(true, 45), true, true);
        var j = J(RemoteProtocol.State(snap));
        Check.Equal("state", j.GetProperty("t").GetString());
        Check.Equal("waitingForGame", j.GetProperty("state").GetString());
        var rec = j.GetProperty("rec");
        Check.True(rec.GetProperty("on").GetBoolean());
        Check.Equal(65, rec.GetProperty("s").GetInt32());
        Check.True(rec.GetProperty("resuming").GetBoolean());
        Check.True(rec.GetProperty("queued").GetBoolean());
        Check.True(rec.GetProperty("finishing").GetBoolean());
        Check.Equal(120, j.GetProperty("bufferS").GetInt32());
        Check.True(j.GetProperty("saving").GetBoolean());
        Check.False(j.GetProperty("screenshots").GetBoolean());
        Check.True(j.GetProperty("cloud").GetBoolean());
        Check.False(j.GetProperty("signedIn").GetBoolean());
        Check.True(j.GetProperty("upload").GetProperty("on").GetBoolean());
        Check.Equal(45, j.GetProperty("upload").GetProperty("p").GetInt32());
        Check.True(j.GetProperty("updatePending").GetBoolean());
        Check.True(j.GetProperty("elevated").GetBoolean());
    }

    [UnitTest]
    public static void Remote_snapshots_with_the_same_values_are_equal()
    {
        Check.True(Sample().Equals(Sample()), "equal values, two instances");
        Check.False(Sample().Equals(Sample(s: 13)));
        Check.False(Sample().Equals(Sample(state: "paused")));
    }

    [UnitTest]
    public static void Remote_welcome_ack_bye_and_the_events_have_the_documented_shape()
    {
        var w = J(RemoteProtocol.Welcome("0.4.6", true, false));
        Check.Equal("welcome", w.GetProperty("t").GetString());
        Check.Equal(1, w.GetProperty("proto").GetInt32());
        Check.Equal("Clink", w.GetProperty("app").GetString());
        Check.Equal("0.4.6", w.GetProperty("version").GetString());
        Check.True(w.GetProperty("dev").GetBoolean());
        Check.False(w.GetProperty("elevated").GetBoolean());
        Check.Equal(7, w.GetProperty("verbs").GetArrayLength());

        var ok = J(RemoteProtocol.Ack(3, null));
        Check.True(ok.GetProperty("ok").GetBoolean());
        Check.False(ok.TryGetProperty("error", out _));
        var bad = J(RemoteProtocol.Ack(4, "rateLimited"));
        Check.False(bad.GetProperty("ok").GetBoolean());
        Check.Equal("rateLimited", bad.GetProperty("error").GetString());
        Check.Equal(4L, bad.GetProperty("id").GetInt64());

        Check.Equal("{\"t\":\"pong\"}", RemoteProtocol.Pong);
        var bye = J(RemoteProtocol.Bye("proto", 1));
        Check.Equal("proto", bye.GetProperty("reason").GetString());
        Check.Equal(1, bye.GetProperty("max").GetInt32());
        Check.False(J(RemoteProtocol.Bye("session")).TryGetProperty("max", out _));

        Check.Equal(30, J(RemoteProtocol.ClipSaved(30)).GetProperty("seconds").GetInt32());
        Check.Equal("paused", J(RemoteProtocol.ClipNotSaved("paused")).GetProperty("reason").GetString());
        Check.Equal("failed", J(RemoteProtocol.ClipNotSaved("C:\\Users\\x\\secret.mp4: disk full")).GetProperty("reason").GetString(), "free text never leaves Clink");
        Check.Equal(50, J(RemoteProtocol.ShareProgress(50)).GetProperty("p").GetInt32());
        Check.Equal(100, J(RemoteProtocol.ShareProgress(500)).GetProperty("p").GetInt32());
        Check.Equal("uploaded", J(RemoteProtocol.ShareDone("uploaded")).GetProperty("outcome").GetString());
        Check.Equal("failed", J(RemoteProtocol.ShareDone("https://copium.example/secret")).GetProperty("outcome").GetString());
        foreach (string o in new[] { "linkCopied", "uploaded", "failed", "refused", "declined", "fileShareOpened", "noClip" })
            Check.Equal(o, J(RemoteProtocol.ShareDone(o)).GetProperty("outcome").GetString());
        foreach (string r in new[] { "restarting", "paused", "waitingForGame", "error", "notReady", "clipsOff", "failed" })
            Check.Equal(r, J(RemoteProtocol.ClipNotSaved(r)).GetProperty("reason").GetString());
    }

    [UnitTest]
    public static void Remote_nothing_sent_looks_like_a_path_or_a_link()
    {
        var all = new List<string>
        {
            RemoteProtocol.Welcome("0.4.6", false, true),
            RemoteProtocol.State(Sample()),
            RemoteProtocol.State(new RemoteSnapshot("error", new RemoteRec(false, 0, false, false, false), 0, false, false, false, false, new RemoteUpload(false, 0), false, false)),
            RemoteProtocol.Ack(1, "unknownVerb"), RemoteProtocol.Pong, RemoteProtocol.Bye("proto", 1),
            RemoteProtocol.ClipSaved(30), RemoteProtocol.ClipNotSaved("error"), RemoteProtocol.ShareProgress(40), RemoteProtocol.ShareDone("linkCopied"),
        };
        var pathLike = new Regex(@"[A-Za-z]:[\\/]|\\\\|https?:|\.(mp4|png|exe|json|log)\b|/", RegexOptions.IgnoreCase);
        foreach (string s in all)
        {
            Check.False(pathLike.IsMatch(s), "path-like text in " + s);
            Check.DoesNotContain(s, "\\", "no backslash in " + s);
        }
    }

    [UnitTest]
    public static void Remote_a_client_name_is_made_safe_to_log()
    {
        Check.Equal("deck 1.0", RemoteProtocol.Sanitize("deck 1.0"));
        Check.Equal("a?b?c", RemoteProtocol.Sanitize("a\nb\u0000c"));
        Check.Equal(40, RemoteProtocol.Sanitize(new string('x', 500)).Length);
        Check.Equal("", RemoteProtocol.Sanitize(null));
    }
}
