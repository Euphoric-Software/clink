using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Clink.Services;

/// <summary>A line a client sent, as far as the server cares: its type and the few fields it reads. Everything else is ignored.</summary>
internal sealed record RemoteMessage(string Type, long? Id, int? Proto, string? Client, string? Version, string? Verb, string? Nonce = null);

/// <summary>
/// The wire format of the remote status pipe (protocol 1): UTF-8 JSON, one message per line. Pure: parsing, serialising, the verb
/// allow-list and the limits; no I/O. Unknown fields and unknown message types are ignored. A client verb is only ever looked up in
/// <see cref="Resolve"/>'s fixed table: the text a client sent never reaches <c>AppController.RunCommand</c>.
/// </summary>
internal static class RemoteProtocol
{
    public const int Proto = 1;
    /// <summary>The longest incoming line (without its line break); a longer one closes the connection.</summary>
    public const int MaxLineBytes = 4096;
    public const int MaxClients = 4;
    /// <summary>Messages waiting for one client; a full queue drops that client.</summary>
    public const int MaxQueued = 32;
    /// <summary>Commands for the whole server (all clients together), so a client that reconnects does not get a fresh burst.</summary>
    public const double CommandsPerSecond = 10;
    public const double CommandBurst = 5;
    /// <summary>Lines one connection may send (any line: hello, ping, cmd, junk), before and after its hello; more closes it.</summary>
    public const double LinesPerSecond = 50;
    public const double LineBurst = 50;
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    /// <summary>The wait after a failed accept (or while another process holds the pipe name); doubles each time up to <see cref="BackoffMax"/>.</summary>
    public static readonly TimeSpan BackoffStart = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(60);

    /// <summary>The verbs a client may send: the command each one runs, and whether it needs the Clips feature. The one table: the welcome's list, the allow-list and the clipsOff check all come from it.</summary>
    private static readonly (string Verb, string Command, bool NeedsClips)[] Table =
    {
        ("clip", "clip", true),
        ("record", "record", true),
        ("pause", "pause", true),
        ("resume", "resume", true),
        ("screenshot", "screenshot", false),
        ("share", "share", true),
        ("show", "show", false),
    };

    private static readonly Dictionary<string, (string Command, bool NeedsClips)> Commands =
        Table.ToDictionary(v => v.Verb, v => (v.Command, v.NeedsClips), StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> Verbs = Table.Select(v => v.Verb).ToArray();

    /// <summary>Verbs that need the Clips feature (the pipe answers "clipsOff" while it is off).</summary>
    public static bool NeedsClips(string verb) => Commands.TryGetValue(verb, out var v) && v.NeedsClips;

    /// <summary>Null when <paramref name="verb"/> is allowed (<paramref name="command"/> is what to run), otherwise the ack error: "notAllowed" for a command Clink knows but remote clients may not run, "unknownVerb" for anything else.</summary>
    public static string? Resolve(string? verb, out string command)
    {
        command = "";
        if (verb == null) return "unknownVerb";
        if (Commands.TryGetValue(verb, out var mapped)) { command = mapped.Command; return null; }
        string v = verb.Trim();
        if (v.StartsWith("screenshot-edit", StringComparison.OrdinalIgnoreCase) || v.Equals("trim", StringComparison.OrdinalIgnoreCase)) return "notAllowed";
        return "unknownVerb";
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>The message in a line, or null when it is not a JSON object with a string "t" (the caller ignores such a line).</summary>
    public static RemoteMessage? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return null;
            long? id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt64(out long idv) ? idv : null;
            int? proto = root.TryGetProperty("proto", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int pv) ? pv : null;
            return new RemoteMessage(t.GetString() ?? "", id, proto, Str(root, "client"), Str(root, "v"), Str(root, "verb"), Str(root, "nonce"));
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>A client's self-description made safe to log: letters, digits and . - _ space, at most 40 characters.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if (sb.Length >= 40) break;
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '?');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ key-file handshake

    public const int NonceMin = 16, NonceMax = 64;
    private const string ProofContext = "clink-remote-proof-v1";

    /// <summary>The bytes of a hello nonce: base64 of 16 to 64 bytes (no whitespace). Anything else is null: the welcome then carries no proof.</summary>
    public static byte[]? ParseNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) || nonce.Length > 88) return null;
        foreach (char c in nonce) if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return null;
        Span<byte> buf = stackalloc byte[NonceMax + 3];
        if (!Convert.TryFromBase64String(nonce, buf, out int n) || n < NonceMin || n > NonceMax) return null;
        return buf[..n].ToArray();
    }

    /// <summary>base64 of HMAC-SHA256(key, UTF-8 "clink-remote-proof-v1" || 0x00 || UTF-8 pipeName (without the \\.\pipe\ prefix) || 0x00 || nonce). Pure; the plugin computes the same from the key file.</summary>
    public static string Proof(byte[] key, string pipeName, byte[] nonce)
    {
        byte[] context = Encoding.UTF8.GetBytes(ProofContext), name = Encoding.UTF8.GetBytes(pipeName);
        var message = new byte[context.Length + 1 + name.Length + 1 + nonce.Length];
        int at = 0;
        context.CopyTo(message, at); at += context.Length;
        message[at++] = 0;
        name.CopyTo(message, at); at += name.Length;
        message[at++] = 0;
        nonce.CopyTo(message, at);
        return Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(key, message));
    }

    // ------------------------------------------------------------------ serialising

    private static string Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer)) write(w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <param name="proof">The key-file proof (<see cref="Proof"/>) for the nonce of the client's hello; null leaves the field out.</param>
    public static string Welcome(string version, bool dev, bool elevated, string? proof = null) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "welcome");
        w.WriteNumber("proto", Proto);
        w.WriteString("app", "Clink");
        w.WriteString("version", version);
        w.WriteBoolean("dev", dev);
        w.WriteBoolean("elevated", elevated);
        if (proof != null) w.WriteString("proof", proof);
        w.WriteStartArray("verbs");
        foreach (string v in Verbs) w.WriteStringValue(v);
        w.WriteEndArray();
        w.WriteEndObject();
    });

    public static string State(RemoteSnapshot s) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "state");
        w.WriteString("state", s.State);
        w.WriteStartObject("rec");
        w.WriteBoolean("on", s.Rec.On);
        w.WriteNumber("s", s.Rec.S);
        w.WriteBoolean("resuming", s.Rec.Resuming);
        w.WriteBoolean("queued", s.Rec.Queued);
        w.WriteBoolean("finishing", s.Rec.Finishing);
        w.WriteEndObject();
        w.WriteNumber("bufferS", s.BufferS);
        w.WriteBoolean("saving", s.Saving);
        w.WriteBoolean("screenshots", s.Screenshots);
        w.WriteBoolean("cloud", s.Cloud);
        w.WriteBoolean("signedIn", s.SignedIn);
        w.WriteStartObject("upload");
        w.WriteBoolean("on", s.Upload.On);
        w.WriteNumber("p", s.Upload.P);
        w.WriteEndObject();
        w.WriteBoolean("updatePending", s.UpdatePending);
        w.WriteBoolean("elevated", s.Elevated);
        w.WriteEndObject();
    });

    public static string Ack(long id, string? error) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "ack");
        w.WriteNumber("id", id);
        w.WriteBoolean("ok", error == null);
        if (error != null) w.WriteString("error", error);
        w.WriteEndObject();
    });

    public const string Pong = "{\"t\":\"pong\"}";

    public static string Bye(string reason, int? max = null) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "bye");
        w.WriteString("reason", reason);
        if (max is int m) w.WriteNumber("max", m);
        w.WriteEndObject();
    });

    // ---- events

    public static string ClipSaved(int seconds) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "clipSaved");
        w.WriteNumber("seconds", Math.Max(0, seconds));
        w.WriteEndObject();
    });

    private static readonly HashSet<string> NotSavedReasons = new(StringComparer.Ordinal)
        { "restarting", "paused", "waitingForGame", "error", "notReady", "clipsOff", "failed" };

    /// <summary>Why a clip was not saved; a reason that is not one of the fixed codes is sent as "failed" (no free text leaves Clink).</summary>
    public static string ClipNotSaved(string reason) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "clipNotSaved");
        w.WriteString("reason", NotSavedReasons.Contains(reason) ? reason : "failed");
        w.WriteEndObject();
    });

    public static string ShareProgress(int percent) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "shareProgress");
        w.WriteNumber("p", Math.Clamp(percent, 0, 100));
        w.WriteEndObject();
    });

    private static readonly HashSet<string> ShareOutcomes = new(StringComparer.Ordinal)
        { "linkCopied", "uploaded", "failed", "refused", "declined", "fileShareOpened", "noClip" };

    public static string ShareDone(string outcome) => Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("t", "shareDone");
        w.WriteString("outcome", ShareOutcomes.Contains(outcome) ? outcome : "failed");
        w.WriteEndObject();
    });
}
