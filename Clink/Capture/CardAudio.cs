using Clink.Audio;

namespace Clink.Capture;

/// <summary>
/// The sound of a capture card: its HDMI audio is a separate Windows capture endpoint, paired with the card's picture by the device
/// container (one physical device = one container) and, where Windows gives none, by name. Pure, so it is unit-tested without a card.
/// </summary>
internal static class CardAudio
{
    /// <summary>Largest A/V offset taken from the hidden setting, in milliseconds either way (a typo cannot push the sound off the picture by minutes).</summary>
    public const int MaxOffsetMs = 500;

    /// <summary>Words every capture device carries; they say nothing about which card it is.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "video", "capture", "camera", "usb", "game", "audio", "hdmi", "device", "card", "input", "digital", "interface", "microphone", "line", "in", "out", "the",
    };

    /// <summary>The container Windows gives every device soldered to or plugged into the PC itself (an internal PCIe card, the onboard inputs): it says nothing about which device it is.</summary>
    public static readonly Guid PcContainer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    private static bool IsRealContainer(Guid? c) => c is { } g && g != Guid.Empty && g != PcContainer;

    /// <summary>
    /// The active capture endpoint that belongs to <paramref name="card"/>: the one in the card's container (when several share it, the one
    /// whose name fits the card); else the only one whose name shares the card's distinctive words (one name holds all of the other's).
    /// Null when none, or when several fit (which may be another card of the same model): it is never guessed.
    /// </summary>
    public static AudioDeviceInfo? Pair(VideoDevice card, IReadOnlyList<AudioDeviceInfo> endpoints)
    {
        var words = Words(card.Name);
        if (IsRealContainer(card.ContainerId))
        {
            var mine = new List<AudioDeviceInfo>();
            foreach (var e in endpoints)
                if (e.Problem == null && e.ContainerId == card.ContainerId) mine.Add(e);
            if (mine.Count == 1) return mine[0];
            if (mine.Count > 1) return ByName(words, mine, card.ContainerId);   // a card with a mic jack: its HDMI or line input by name, else none
        }
        if (words.Count == 0) return null;
        return ByName(words, endpoints, IsRealContainer(card.ContainerId) ? card.ContainerId : null);
    }

    /// <summary>The only active endpoint whose name fits <paramref name="words"/>; null when none or several. An endpoint in a real container other than <paramref name="mine"/> is another device's.</summary>
    private static AudioDeviceInfo? ByName(HashSet<string> words, IReadOnlyList<AudioDeviceInfo> endpoints, Guid? mine)
    {
        if (words.Count == 0) return null;
        AudioDeviceInfo? found = null;
        foreach (var e in endpoints)
        {
            if (e.Problem != null) continue;
            if (mine != null && IsRealContainer(e.ContainerId) && e.ContainerId != mine) continue;
            var theirWords = Words(e.Name);
            if (theirWords.Count == 0 || !(words.All(theirWords.Contains) || theirWords.All(words.Contains))) continue;   // either name may be the shorter ("Elgato HD60 X" / "Game Capture HD60 X")
            if (found != null) return null;
            found = e;
        }
        return found;
    }

    /// <summary>Whether the Mic would record the card's own endpoint (chosen by id, or "default" while the default input is the card): then the Mic is left out so the sound is not in the mix twice.</summary>
    public static bool MicIsCardAudio(string? micDeviceId, string? cardAudioId, string? defaultCaptureId)
    {
        if (string.IsNullOrEmpty(cardAudioId)) return false;
        string? mic = string.IsNullOrEmpty(micDeviceId) ? defaultCaptureId : micDeviceId;
        return string.Equals(mic, cardAudioId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The endpoint to record the card's audio from (its Windows id), or null when none is found. Lists the capture endpoints; call off any hot path.</summary>
    public static AudioDeviceInfo? Find(VideoDevice card)
    {
        try { return Pair(card, WasapiSource.ListDevices(capture: true)); }
        catch (Exception ex) { Log.Warn("capture card audio: " + ex.Message); return null; }
    }

    /// <summary>The offset setting in a range the engine takes.</summary>
    public static int ClampOffsetMs(int ms) => Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);

    /// <summary>
    /// A video frame's QPC time (100 ns units) moved by the A/V offset: a positive offset shows the picture later (the sound runs late),
    /// a negative one earlier. The sound's own times are never moved.
    /// </summary>
    public static long ApplyOffset(long qpc100ns, int offsetMs) => qpc100ns + ClampOffsetMs(offsetMs) * 10_000L;

    /// <summary>The distinctive lower-case words of a device name ("Elgato HD60 X" gives elgato and hd60).</summary>
    private static HashSet<string> Words(string name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var cur = new System.Text.StringBuilder();
        void Flush()
        {
            if (cur.Length > 1 && !Generic.Contains(cur.ToString())) set.Add(cur.ToString());
            cur.Clear();
        }
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c)) cur.Append(char.ToLowerInvariant(c)); else Flush();
        }
        Flush();
        return set;
    }
}
