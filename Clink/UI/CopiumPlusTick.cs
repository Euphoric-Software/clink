using Clink.Services;

namespace Clink.UI;

/// <summary>
/// When the Copium Plus size target ticks "I have Copium+" (the account has Plus, so Share checks Plus's limits). Only a deliberate
/// choice does: a pick from the open list, or stopping on it with the arrow / letter keys on the closed box and moving on. A
/// keyboard user who steps through the list on the way to another target must not be left with the account setting changed.
/// Pure state, so the rule is tested (<c>CaptureTabTests</c>); the Capture tab feeds it the combo's events.
/// </summary>
internal sealed class CopiumPlusTick
{
    private bool _pending;   // Copium Plus was reached with the keys on the closed box and is not ticked yet

    /// <summary>
    /// The size target changed to <paramref name="targetId"/>. <paramref name="fromOpenList"/>: the drop-down list is open (a pick,
    /// not a key on the closed box). Returns true when "I have Copium+" is to be ticked now.
    /// </summary>
    public bool Selected(string? targetId, bool fromOpenList, bool alreadyTicked)
    {
        bool plus = IsCopiumPlus(targetId) && !alreadyTicked;
        _pending = plus && !fromOpenList;
        return plus && fromOpenList;
    }

    /// <summary>
    /// The size-target box lost the keyboard focus. Returns true when the user stopped on Copium Plus with the keys (and it is still
    /// selected and not ticked): that is a choice, so it is ticked now. Merely focusing the box later never ticks it.
    /// </summary>
    public bool FocusLeft(string? targetId, bool alreadyTicked)
    {
        bool tick = _pending && !alreadyTicked && IsCopiumPlus(targetId);
        _pending = false;
        return tick;
    }

    /// <summary>
    /// "I have Copium+" is the account's while signed in with "Sign in with Copium" (a token is stored and came from Connect): the size target
    /// must not tick it. Pass this as part of <c>alreadyTicked</c>.
    /// </summary>
    public static bool AccountOwnsPlus(bool linkedViaConnect, bool hasToken) => linkedViaConnect && hasToken;

    private static bool IsCopiumPlus(string? targetId) => Capabilities.SizeTargetFor(targetId).Id == "copiumplus";
}
