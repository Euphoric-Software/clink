using Clink.Engine;

namespace Clink.Tests;

/// <summary>Engine knobs for two engines side by side: the ring budget and the clip / recording base names (pure rules).</summary>
internal static class EngineKnobsTests
{
    private const long Half = 8L << 30;   // a pretend "half of RAM"

    [UnitTest]
    public static void RingLimit_without_a_budget_is_half_of_RAM()
    {
        Check.Equal(Half, EngineConfig.RingLimit(null, Half), "null");
        Check.Equal(Half, EngineConfig.RingLimit(0, Half), "zero");
        Check.Equal(Half, EngineConfig.RingLimit(-5, Half), "negative");
    }

    [UnitTest]
    public static void RingLimit_budget_is_used_but_never_above_half_of_RAM_or_below_the_minimum()
    {
        Check.Equal(Half / 2, EngineConfig.RingLimit(Half / 2, Half), "half of the limit (Both mode)");
        Check.Equal(Half, EngineConfig.RingLimit(Half * 3, Half), "above the limit");
        Check.Equal(RingStore.MinBytes, EngineConfig.RingLimit(1, Half), "below the minimum");
    }

    [UnitTest]
    public static void GameForName_ignoring_the_hint_drops_the_engines_own_game_too()
    {
        Check.Equal("Doom", EngineConfig.GameForName(false, "Doom", "Hint"), "engine game wins");
        Check.Equal("Hint", EngineConfig.GameForName(false, null, "Hint"), "hint covers");
        Check.Equal("", EngineConfig.GameForName(false, null, null), "none");
        Check.Equal("", EngineConfig.GameForName(true, "Doom", "Hint"), "ignored: engine game");
        Check.Equal("", EngineConfig.GameForName(true, null, "Hint"), "ignored: hint");
    }

    [UnitTest]
    public static void NoGameTrack_only_for_a_card_engine_that_ignores_the_hint_without_card_audio()
    {
        Check.True(EngineConfig.NoGameTrack(true, true, false), "card, ignore, no audio");
        Check.True(!EngineConfig.NoGameTrack(true, true, true), "card audio paired");
        Check.True(!EngineConfig.NoGameTrack(true, false, false), "single-source card unchanged");
        Check.True(!EngineConfig.NoGameTrack(false, true, false), "screen");
        Check.True(!EngineConfig.NoGameTrack(false, false, false), "screen default");
    }

    [UnitTest]
    public static void NameBase_game_wins_then_configured_name_then_default()
    {
        Check.Equal("Doom", EngineConfig.NameBase("Doom", "Elgato", "Clip"), "game");
        Check.Equal("Elgato", EngineConfig.NameBase("", "Elgato", "Clip"), "configured");
        Check.Equal("Elgato", EngineConfig.NameBase(null, "  Elgato ", "Clip"), "trimmed");
        Check.Equal("Clip", EngineConfig.NameBase(null, null, "Clip"), "default null");
        Check.Equal("Recording", EngineConfig.NameBase("", "  ", "Recording"), "default blank");
    }
}
