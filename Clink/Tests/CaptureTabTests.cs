using Clink.UI;

namespace Clink.Tests;

/// <summary>The Capture tab's own rules that do not need a window: when the Copium Plus size target ticks "I have Copium+".</summary>
internal static class CaptureTabTests
{
    [UnitTest]
    public static void A_pick_from_the_open_list_ticks_I_have_Copium_plus_at_once()
    {
        var tick = new CopiumPlusTick();
        Check.True(tick.Selected("copiumplus", fromOpenList: true, alreadyTicked: false), "Copium Plus picked from the list");
        Check.False(tick.Selected("copiumplus", fromOpenList: true, alreadyTicked: true), "nothing to tick when it already is");
        Check.False(tick.Selected("copium200", fromOpenList: true, alreadyTicked: false), "another target");
        Check.False(tick.Selected("none", fromOpenList: true, alreadyTicked: false));
        Check.False(tick.FocusLeft("copiumplus", alreadyTicked: false), "a pick needs no second tick when the box loses the focus");
    }

    [UnitTest]
    public static void Stepping_onto_Copium_plus_with_the_keys_ticks_only_when_the_user_stops_there()
    {
        var tick = new CopiumPlusTick();
        // arrow keys on the closed box: ... Discord Nitro, then Copium Plus: not ticked yet
        Check.False(tick.Selected("discord500", fromOpenList: false, alreadyTicked: false));
        Check.False(tick.Selected("copiumplus", fromOpenList: false, alreadyTicked: false), "reached with the keys");
        Check.True(tick.FocusLeft("copiumplus", alreadyTicked: false), "the user moved on with Copium Plus selected: that is a choice");
        Check.False(tick.FocusLeft("copiumplus", alreadyTicked: true), "once");
    }

    [UnitTest]
    public static void Passing_over_Copium_plus_on_the_way_to_another_target_never_ticks()
    {
        var tick = new CopiumPlusTick();
        Check.False(tick.Selected("copiumplus", fromOpenList: false, alreadyTicked: false));
        Check.False(tick.Selected("discord500", fromOpenList: false, alreadyTicked: false), "stepped on to another target");
        Check.False(tick.FocusLeft("discord500", alreadyTicked: false), "left the box on Discord Nitro: nothing is ticked");
        // a pending step that was undone, even when the box is left showing Copium Plus again by another route
        Check.False(tick.Selected("copiumplus", fromOpenList: false, alreadyTicked: false));
        Check.False(tick.FocusLeft("copium200", alreadyTicked: false), "the target is not Copium Plus any more");
    }

    [UnitTest]
    public static void Focusing_the_box_never_ticks_a_box_the_user_unticked()
    {
        var tick = new CopiumPlusTick();
        // Copium Plus is the target and the user unticked "I have Copium+" on purpose: coming and going with the focus leaves it alone
        Check.False(tick.FocusLeft("copiumplus", alreadyTicked: false), "no key was pressed on the box");
        Check.False(tick.FocusLeft("copiumplus", alreadyTicked: false));
        // a pick of another target and back from the list ticks again: that is a new pick
        Check.False(tick.Selected("copium200", fromOpenList: true, alreadyTicked: false));
        Check.True(tick.Selected("copiumplus", fromOpenList: true, alreadyTicked: false));
    }
}
