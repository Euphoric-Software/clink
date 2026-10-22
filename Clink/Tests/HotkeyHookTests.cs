using Clink.Services;

namespace Clink.Tests;

/// <summary>
/// What the keyboard hook does with each key (<see cref="Hotkeys.Decide"/>, which touches no state): eat or pass it on, fire a
/// binding or hand the key to a capture, and whether the eaten key gets the Alt menu mask. The hook itself only applies the verdict.
/// </summary>
internal static class HotkeyHookTests
{
    private const int C = 0x43, A = 0x41, F5 = 0x74, F8 = 0x77, F9 = 0x78, E8 = 0xE8, Tab = 0x09, Esc = 0x1B, Enter = 0x0D, Space = 0x20;
    private const int ScrollLock = 0x91, Break = 0x03, LAlt = 0xA4, Shift = 0x10;

    private static readonly Func<bool> Desktop = () => false;
    private static readonly Func<bool> InGame = () => true;

    private static HotkeyAction Bind(int vk, bool alt = false, bool ctrl = false, bool shift = false, string action = "clip", int seconds = 0) =>
        new() { Keys = new HotkeyBinding { Vk = vk, Alt = alt, Ctrl = ctrl, Shift = shift }, Action = action, Seconds = seconds };

    private static Hotkeys.KeyInput Press(int vk, bool alt = false, bool ctrl = false, bool shift = false, bool win = false, bool swallowed = false, bool marked = false, int raw = 0) =>
        new(vk, raw == 0 ? vk : raw, true, ctrl, alt, shift, win, marked, swallowed);

    private static Hotkeys.KeyInput Release(int vk, bool swallowed = false, bool marked = false) =>
        new(vk, vk, false, false, false, false, false, marked, swallowed);

    private static Hotkeys.KeyDecision Decide(Hotkeys.KeyInput key, HotkeyAction[]? bindings = null, bool capture = false, bool game = false) =>
        Hotkeys.Decide(key, capture, bindings ?? Array.Empty<HotkeyAction>(), game ? InGame : Desktop);

    private static void Is(Hotkeys.KeyVerdict verdict, Hotkeys.KeyDecision actual, string? what = null) => Check.Equal(verdict, actual.Verdict, what);

    // ------------------------------------------------------------------ binding, mask

    [UnitTest]
    public static void A_bound_key_fires_and_is_eaten_with_the_menu_mask_when_Alt_is_held()
    {
        var alt = Bind(C, alt: true);
        var d = Decide(Press(C, alt: true), new[] { alt });
        Is(Hotkeys.KeyVerdict.Fire, d);
        Check.True(ReferenceEquals(alt, d.Binding), "the binding that fires");
        Check.True(d.Mask, "Alt+C is masked");

        Check.True(Decide(Press(F9, alt: true, shift: true), new[] { Bind(F9, alt: true, shift: true) }).Mask, "Alt+Shift+F9 is masked too");
    }

    [UnitTest]
    public static void No_mask_without_Alt_or_with_Ctrl_held()
    {
        foreach (var (key, binding) in new[]
        {
            (Press(F9), Bind(F9)),
            (Press(F9, shift: true), Bind(F9, shift: true)),
            (Press(F8, ctrl: true), Bind(F8, ctrl: true)),
            (Press(F8, alt: true, ctrl: true), Bind(F8, alt: true, ctrl: true)),   // Ctrl+Alt never opens a menu bar
        })
        {
            var d = Decide(key, new[] { binding });
            Is(Hotkeys.KeyVerdict.Fire, d, binding.Keys.ToString());
            Check.False(d.Mask, binding.Keys + " is not masked");
        }
    }

    [UnitTest]
    public static void No_mask_while_the_game_is_in_front_but_the_key_is_still_eaten()
    {
        var alt = Bind(C, alt: true);

        var onDesktop = Decide(Press(C, alt: true), new[] { alt });
        Check.True(onDesktop.Mask, "a desktop program gets the mask");

        var inGame = Decide(Press(C, alt: true), new[] { alt }, game: true);
        Is(Hotkeys.KeyVerdict.Fire, inGame, "the hotkey still fires in a game");
        Check.True(ReferenceEquals(alt, inGame.Binding));
        Check.False(inGame.Mask, "nothing synthetic is sent to a game");

        // a capture in the editor: the same
        var captured = Decide(Press(F8, alt: true), capture: true, game: true);
        Is(Hotkeys.KeyVerdict.Capture, captured);
        Check.False(captured.Mask);
        Check.True(Decide(Press(F8, alt: true), capture: true).Mask, "the same capture on the desktop is masked");

        // in a game a plain F9 binding also fires with Alt held (the extra-modifier fallback): still no mask
        var fallback = Decide(Press(F9, alt: true), new[] { Bind(F9) }, game: true);
        Is(Hotkeys.KeyVerdict.Fire, fallback);
        Check.False(fallback.Mask);
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(F9, alt: true), new[] { Bind(F9) }), "on the desktop Alt+F9 is the program's");
    }

    [UnitTest]
    public static void A_repeat_of_an_eaten_key_is_eaten_again_without_firing_or_masking_again()
    {
        var bindings = new[] { Bind(C, alt: true) };
        bool swallowed = false;   // what the hook remembers for the key

        var first = Decide(Press(C, alt: true, swallowed: swallowed), bindings);
        Is(Hotkeys.KeyVerdict.Fire, first);
        Check.True(first.Mask, "the first press is masked");
        swallowed = true;

        for (int repeat = 1; repeat <= 5; repeat++)
        {
            var d = Decide(Press(C, alt: true, swallowed: swallowed), bindings);
            Is(Hotkeys.KeyVerdict.EatRepeat, d, $"repeat {repeat}");
            Check.False(d.Mask, $"repeat {repeat} is not masked again");
            Check.Null(d.Binding, $"repeat {repeat} fires nothing");
        }
        Is(Hotkeys.KeyVerdict.EatRepeat, Decide(Press(C, swallowed: true), bindings), "still eaten after Alt was let go while C is held");
        Is(Hotkeys.KeyVerdict.EatRepeat, Decide(Press(C, alt: true, swallowed: true), bindings, game: true), "and in a game");

        var up = Decide(Release(C, swallowed: swallowed), bindings);
        Is(Hotkeys.KeyVerdict.EatRelease, up);
        Check.False(up.Mask);
        swallowed = false;

        var again = Decide(Press(C, alt: true, swallowed: swallowed), bindings);
        Is(Hotkeys.KeyVerdict.Fire, again, "a new press fires again");
        Check.True(again.Mask, "and is masked again");
    }

    [UnitTest]
    public static void A_release_is_eaten_only_when_its_press_was()
    {
        var bindings = new[] { Bind(C, alt: true) };
        Is(Hotkeys.KeyVerdict.Pass, Decide(Release(C, swallowed: false), bindings), "the program saw this key go down: it sees it come up");
        Is(Hotkeys.KeyVerdict.EatRelease, Decide(Release(C, swallowed: true), bindings));
        Is(Hotkeys.KeyVerdict.EatRelease, Decide(Release(C, swallowed: true)), "even when the binding is gone by now");
        Is(Hotkeys.KeyVerdict.EatRelease, Decide(Release(C, swallowed: true), bindings, game: true));
        Is(Hotkeys.KeyVerdict.Pass, Decide(Release(F5, swallowed: false), bindings));
    }

    [UnitTest]
    public static void Modifiers_and_keys_nothing_is_bound_to_go_on()
    {
        var bindings = new[] { Bind(C, alt: true) };
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(LAlt, alt: true), bindings), "the Alt key itself");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(Shift, shift: true), bindings));
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(F5), bindings), "a key nothing is bound to");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(C), bindings), "C without Alt");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(C, alt: true)), "no bindings at all");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(0), bindings), "no key");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(300), bindings), "a code outside 1..255");
        Check.False(Decide(Press(F5), bindings).Mask);
    }

    // ------------------------------------------------------------------ the menu mask key itself, key code E8

    [UnitTest]
    public static void The_dummy_key_of_the_mask_is_never_a_hotkey_never_captured_and_never_eaten()
    {
        var bindings = new[] { Bind(E8), Bind(C, alt: true) };
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(E8, alt: true, marked: true), bindings), "the injected key goes straight on");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Release(E8, marked: true), bindings));
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(E8, alt: true, marked: true), bindings, capture: true), "a capture does not take it");
        // whatever carries the tag is passed on, whatever key it claims to be
        var tagged = Decide(Press(C, alt: true, marked: true), bindings);
        Is(Hotkeys.KeyVerdict.Pass, tagged, "a tagged C does not fire the Alt+C binding");
        Check.False(tagged.Mask);
        Is(Hotkeys.KeyVerdict.Pass, Decide(Release(C, swallowed: true, marked: true), bindings), "nor does a tagged release touch what was eaten");
    }

    [UnitTest]
    public static void Key_code_E8_is_reserved_it_is_never_a_hotkey_or_a_capturable_key()
    {
        // AutoHotkey sends E8 as its own menu-mask key (without our tag): it is not ours to bind, take or eat
        var bound = new[] { Bind(E8), Bind(E8, alt: true) };
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(E8), bound), "not a hotkey (a hand-edited file may say so)");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(E8, alt: true), bound));
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(E8, alt: true), bound, game: true));
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(E8), capture: true), "not taken by a capture, and not refused either");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(E8, alt: true), capture: true));
        Is(Hotkeys.KeyVerdict.Pass, Decide(Release(E8)), "its release goes on");

        // a binding on it is dropped when the list is set (and said so), so Actions never shows a hotkey that cannot fire
        var before = Hotkeys.Actions.ToArray();
        try
        {
            Hotkeys.Rebind(new[] { Bind(E8), Bind(C, alt: true) });
            Check.Equal(1, Hotkeys.Actions.Count);
            Check.Equal(C, Hotkeys.Actions[0].Keys.Vk);
        }
        finally { Hotkeys.Rebind(before); }
    }

    // ------------------------------------------------------------------ capture

    [UnitTest]
    public static void A_capture_takes_what_is_not_reserved_and_masks_it_with_Alt()
    {
        var alt = Decide(Press(F8, alt: true), capture: true);
        Is(Hotkeys.KeyVerdict.Capture, alt);
        Check.True(alt.Mask, "Alt+F8");
        Check.False(Decide(Press(F8), capture: true).Mask, "F8 alone needs no mask");
        Is(Hotkeys.KeyVerdict.Capture, Decide(Press(F8), capture: true));
        var ctrlAlt = Decide(Press(F8, alt: true, ctrl: true), capture: true);
        Is(Hotkeys.KeyVerdict.Capture, ctrlAlt);
        Check.False(ctrlAlt.Mask, "Ctrl+Alt+F8");
        Is(Hotkeys.KeyVerdict.Capture, Decide(Press(A, ctrl: true), capture: true), "Ctrl+A is fine");
        Is(Hotkeys.KeyVerdict.Capture, Decide(Press(Esc), capture: true), "Esc is reported: the editor cancels on it");
    }

    [UnitTest]
    public static void A_capture_refuses_bare_typing_keys_and_leaves_the_systems_combos_alone()
    {
        Is(Hotkeys.KeyVerdict.Refuse, Decide(Press(A), capture: true), "a bare letter: the user is told, the key goes on");
        Is(Hotkeys.KeyVerdict.Refuse, Decide(Press(A, shift: true), capture: true), "Shift only picks the character");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(Tab, alt: true), capture: true), "Alt+Tab is the system's, and not a typing key");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(Esc, alt: true), capture: true), "Alt+Esc");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(F8, win: true), capture: true), "a Win chord");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(Enter), capture: true), "a bare Enter");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(Space), capture: true), "a bare Space");
        foreach (var d in new[] { Decide(Press(A), capture: true), Decide(Press(Tab, alt: true), capture: true), Decide(Press(F8, win: true), capture: true) })
            Check.False(d.Mask, "a key that goes on is never masked");
    }

    [UnitTest]
    public static void A_bound_key_does_not_fire_while_a_capture_is_armed()
    {
        var bound = new[] { Bind(F9) };
        Is(Hotkeys.KeyVerdict.Fire, Decide(Press(F9), bound));
        var d = Decide(Press(F9), bound, capture: true);
        Is(Hotkeys.KeyVerdict.Capture, d, "the capture gets it, the binding does not fire");
        Check.Null(d.Binding);
    }

    // ------------------------------------------------------------------ matching (as before)

    [UnitTest]
    public static void Extra_modifiers_still_fire_a_binding_only_while_the_game_is_in_front()
    {
        var scroll = Bind(ScrollLock);
        // crouching (Ctrl) while pressing ScrollLock: the system reports Ctrl+Break, the hook calls it ScrollLock
        var crouch = Press(ScrollLock, ctrl: true, raw: Break);
        Is(Hotkeys.KeyVerdict.Pass, Decide(crouch, new[] { scroll }), "on the desktop Ctrl+ScrollLock belongs to the program in front");
        var d = Decide(crouch, new[] { scroll }, game: true);
        Is(Hotkeys.KeyVerdict.Fire, d, "in the game it still clips");
        Check.True(ReferenceEquals(scroll, d.Binding));

        // the exact combo wins: F9 and Shift+F9 are different bindings
        var plain = Bind(F9);
        var shifted = Bind(F9, shift: true);
        Check.True(ReferenceEquals(plain, Decide(Press(F9), new[] { shifted, plain }).Binding));
        Check.True(ReferenceEquals(shifted, Decide(Press(F9, shift: true), new[] { plain, shifted }).Binding));
        // of the fallback candidates the one with the most modifiers held wins
        Check.True(ReferenceEquals(shifted, Decide(Press(F9, shift: true, ctrl: true), new[] { plain, shifted }, game: true).Binding));

        // a binding saved as Ctrl+Break before ScrollLock and Pause were told apart still fires on its raw key code
        var oldBreak = Bind(Break, ctrl: true);
        Check.True(ReferenceEquals(oldBreak, Decide(crouch, new[] { oldBreak }).Binding));

        // never a Win chord, never a combo the system owns
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(F9, win: true), new[] { plain }, game: true), "Win+F9");
        Is(Hotkeys.KeyVerdict.Pass, Decide(Press(0x73, alt: true), new[] { Bind(0x73) }, game: true), "Alt+F4 with F4 bound");
    }
}
