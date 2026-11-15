using Clink.Audio;
using Clink.Capture;

namespace Clink.Tests;

/// <summary>A capture card's HDMI audio without a card: the pairing of its endpoint (container, name fallback), the Mic overlap and the A/V offset.</summary>
internal static class CardAudioTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111"), B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static VideoDevice Card(string name, Guid? c) => new(@"\\?\usb#" + name, name, c);
    private static AudioDeviceInfo Ep(string id, string name, Guid? c, AudioProblemKind p = AudioProblemKind.None) => new(id, name, p, c);

    [UnitTest]
    public static void The_endpoint_in_the_cards_container_is_paired_whatever_it_is_called()
    {
        var eps = new[] { Ep("mic", "Microphone (Realtek)", B), Ep("hdmi", "Digital Audio Interface (USB Video)", A) };
        Check.Equal("hdmi", CardAudio.Pair(Card("Elgato HD60 X", A), eps)?.Id, "the container decides");
    }

    [UnitTest]
    public static void Several_cards_each_get_their_own_endpoint()
    {
        var eps = new[] { Ep("a", "HDMI (Game Capture HD60 X)", A), Ep("b", "HDMI (Game Capture HD60 X)", B) };
        Check.Equal("a", CardAudio.Pair(Card("Elgato HD60 X", A), eps)?.Id);
        Check.Equal("b", CardAudio.Pair(Card("Elgato HD60 X", B), eps)?.Id, "same model, other container");
    }

    [UnitTest]
    public static void Without_a_container_the_name_pairs_when_it_is_unambiguous()
    {
        var eps = new[] { Ep("mic", "Microphone (Realtek)", null), Ep("hdmi", "Digital Audio Interface (Game Capture HD60 X)", null) };
        Check.Equal("hdmi", CardAudio.Pair(Card("Elgato HD60 X", null), eps)?.Id, "the card's name holds the endpoint's distinctive words");
        Check.Equal("hdmi", CardAudio.Pair(Card("Elgato HD60 X", A), eps)?.Id, "no container on the endpoints: the name still pairs");
    }

    [UnitTest]
    public static void No_pairing_when_nothing_fits_or_the_name_is_ambiguous()
    {
        var other = new[] { Ep("mic", "Microphone (Realtek)", B), Ep("spk", "Line In (USB Audio)", null) };
        Check.Null(CardAudio.Pair(Card("Elgato HD60 X", A), other), "no match");
        Check.Null(CardAudio.Pair(Card("Elgato HD60 X", A), Array.Empty<AudioDeviceInfo>()), "no endpoints");
        var twins = new[] { Ep("a", "HDMI (Game Capture HD60 X)", null), Ep("b", "HDMI (Game Capture HD60 X)", null) };
        Check.Null(CardAudio.Pair(Card("Elgato HD60 X", null), twins), "two alike names and no container: not guessed");
        Check.Null(CardAudio.Pair(Card("USB Video", null), new[] { Ep("x", "USB Audio", null) }), "generic words alone pair nothing");
    }

    [UnitTest]
    public static void An_endpoint_of_another_container_or_one_not_connected_is_not_paired_by_name()
    {
        var eps = new[] { Ep("b", "HDMI (Game Capture HD60 X)", B), Ep("gone", "HDMI (Game Capture HD60 X)", null, AudioProblemKind.NotConnected) };
        Check.Null(CardAudio.Pair(Card("Elgato HD60 X", A), eps));
        Check.Equal("gone", CardAudio.Pair(Card("Elgato HD60 X", A), new[] { eps[1] with { ProblemKind = AudioProblemKind.None } })?.Id);
    }

    [UnitTest]
    public static void A_pcie_card_shares_the_pcs_container_with_the_onboard_inputs_and_is_paired_by_name_or_not_at_all()
    {
        var pc = CardAudio.PcContainer;
        var card = Card("Elgato 4K60 Pro", pc);
        var eps = new[] { Ep("mic", "Microphone (Realtek High Definition Audio)", pc), Ep("hdmi", "Digital Audio Interface (Game Capture 4K60 Pro)", pc) };
        Check.Equal("hdmi", CardAudio.Pair(card, eps)?.Id, "the name picks the card's input, not the first in the shared container");
        Check.Null(CardAudio.Pair(card, new[] { eps[0] }), "only the motherboard mic: not paired");
        Check.Null(CardAudio.Pair(card, new[] { eps[0], Ep("line", "Line In (Realtek High Definition Audio)", pc) }), "two onboard inputs: none");
    }

    [UnitTest]
    public static void A_usb_card_with_a_mic_jack_gives_its_hdmi_input_by_name_or_none()
    {
        var card = Card("Elgato HD60 X", A);
        var jack = Ep("jack", "Mic In (USB Audio)", A);
        var hdmi = Ep("hdmi", "HDMI (Game Capture HD60 X)", A);
        Check.Equal("hdmi", CardAudio.Pair(card, new[] { jack, hdmi })?.Id, "two endpoints in the container: the HDMI one by name");
        Check.Null(CardAudio.Pair(card, new[] { Ep("j2", "Line In (Game Capture HD60 X)", A), hdmi }), "both names fit the card: not guessed");
        Check.Null(CardAudio.Pair(card, new[] { Ep("l1", "Line In (USB Audio)", A), Ep("l2", "Mic In (USB Audio)", A) }), "two in the container and no name settles it: not guessed");
    }

    [UnitTest]
    public static void The_mic_is_left_out_when_it_is_the_cards_own_input()
    {
        Check.True(CardAudio.MicIsCardAudio("hdmi", "hdmi", null), "chosen by id");
        Check.True(CardAudio.MicIsCardAudio(null, "hdmi", "hdmi"), "the default input is the card");
        Check.True(CardAudio.MicIsCardAudio("", "HDMI", "hdmi"), "case");
        Check.False(CardAudio.MicIsCardAudio(null, "hdmi", "mic"), "another default");
        Check.False(CardAudio.MicIsCardAudio("mic", "hdmi", "hdmi"), "a chosen mic wins over the default");
        Check.False(CardAudio.MicIsCardAudio(null, null, null), "no card audio");
    }

    [UnitTest]
    public static void The_offset_moves_the_picture_time_and_is_bounded()
    {
        Check.Equal(1_000_000L, CardAudio.ApplyOffset(1_000_000L, 0));
        Check.Equal(1_400_000L, CardAudio.ApplyOffset(1_000_000L, 40), "positive: the picture shows later");
        Check.Equal(600_000L, CardAudio.ApplyOffset(1_000_000L, -40), "negative: earlier");
        Check.Equal(CardAudio.MaxOffsetMs, CardAudio.ClampOffsetMs(int.MaxValue));
        Check.Equal(-CardAudio.MaxOffsetMs, CardAudio.ClampOffsetMs(int.MinValue));
        Check.Equal(1_000_000L + 5_000_000L, CardAudio.ApplyOffset(1_000_000L, 99999), "a typo cannot push it past the bound");
    }
}
