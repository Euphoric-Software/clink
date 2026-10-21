using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Clink.Services;

/// <summary>
/// Global low-level keyboard hook (works while games have focus, including injected keys from Stream Deck). Matches any of
/// a list of bindings (<see cref="HotkeyAction"/>: key combo + what to do) and reports which one was pressed.
/// Modifier state is read live (GetAsyncKeyState) when a key is matched: the hook only sees the current desktop, so the
/// key-ups of a Win+L / Ctrl+Alt+Del / UAC chord never arrive and tracked flags would stay stuck. A key-up is swallowed only
/// when its key-down was, so a game never ends up with a key it saw go down but not up.
/// </summary>
internal static unsafe class Hotkeys
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_DELETE = 0x2E;

    private static HHOOK s_hook;
    private static HotkeyAction[] s_actions = Array.Empty<HotkeyAction>();
    private static Action<HotkeyAction>? s_onPressed;
    private static readonly bool[] s_swallowed = new bool[256];   // keys whose key-down we ate (binding fired, or capture): their key-up is eaten too, auto-repeat does not re-fire
    private static Action<int, bool, bool, bool, bool>? s_captureNext;
    private static bool s_sessionHooked;

    /// <summary>The bindings the hook currently matches (snapshot).</summary>
    public static IReadOnlyList<HotkeyAction> Actions => s_actions;

    /// <summary>Installs the hook (once) and sets the bindings. <paramref name="onPressed"/> runs on the hook thread: marshal to the UI.</summary>
    public static void Install(IReadOnlyList<HotkeyAction> actions, Action<HotkeyAction> onPressed)
    {
        Rebind(actions);
        s_onPressed = onPressed;
        if (s_hook.IsNull)
        {
            s_hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, default, 0);
            if (s_hook.IsNull) Log.Warn("Keyboard hook failed: " + Marshal.GetLastWin32Error());
        }
        if (!s_sessionHooked)
        {
            // Lock / unlock / switch user: whatever was held when the desktop changed is not held any more as far as we know.
            try { SystemEvents.SessionSwitch += (_, _) => Reset(); s_sessionHooked = true; }
            catch (Exception ex) { Log.Warn("hotkeys: session events: " + ex.Message); }
        }
    }

    /// <summary>Forgets every half-seen key (after a desktop switch nothing we tracked can be trusted).</summary>
    public static void Reset() => Array.Clear(s_swallowed);

    /// <summary>Replaces the bindings (after the list in settings changed). Entries without a key are skipped.</summary>
    public static void Rebind(IReadOnlyList<HotkeyAction> actions) =>
        s_actions = actions.Where(a => a?.Keys != null && a.Keys.Vk > 0).ToArray();

    /// <summary>v1 single-hotkey API: changes the keys of the first "clip" binding (or adds one). The v2 UI uses <see cref="Rebind(IReadOnlyList{HotkeyAction})"/>.</summary>
    public static void Rebind(HotkeyBinding binding)
    {
        var list = s_actions.ToList();
        var clip = list.FirstOrDefault(a => IsClip(a.Action));
        if (clip != null) clip.Keys = binding;
        else list.Add(new HotkeyAction { Keys = binding, Action = "clip", Seconds = 0 });
        Rebind(list);
    }

    public static bool IsClip(string? action) => string.IsNullOrEmpty(action) || action.Equals("clip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The next non-modifier key press is reported (with modifiers) and swallowed; used by the "change hotkey" UI. Combos the
    /// system owns (Alt+Tab, Alt+Esc, Alt+F4, Ctrl+Esc, Ctrl+Shift+Esc, any Win chord) and a plain Enter / Space / Tab are
    /// passed through untouched and leave the capture armed. Esc is reported (the UI cancels on it).
    /// </summary>
    public static void CaptureNext(Action<int, bool, bool, bool, bool>? handler) => s_captureNext = handler;

    public static void Uninstall()
    {
        if (!s_hook.IsNull) { PInvoke.UnhookWindowsHookEx(s_hook); s_hook = default; }
        Reset();
    }

    private static bool IsModifier(int vk) => vk is 0xA0 or 0xA1 or VK_SHIFT or 0xA2 or 0xA3 or VK_CONTROL or 0xA4 or 0xA5 or VK_MENU or VK_LWIN or VK_RWIN;

    private static bool IsDown(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>A key the capture must not take: it belongs to the shell / window manager, or it is a bare editing key.</summary>
    private static bool IsReservedForCapture(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        if (win) return true;
        if (alt && vk is VK_TAB or VK_ESCAPE or VK_F4) return true;
        if (ctrl && vk == VK_ESCAPE) return true;
        if (ctrl && alt && vk == VK_DELETE) return true;
        if (!ctrl && !alt && !shift && vk is VK_TAB or VK_RETURN or VK_SPACE) return true;
        return false;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static LRESULT HookProc(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            try
            {
                var ks = (KBDLLHOOKSTRUCT*)lParam.Value;
                uint msg = (uint)wParam.Value;
                bool down = msg == PInvoke.WM_KEYDOWN || msg == PInvoke.WM_SYSKEYDOWN;
                int vk = (int)ks->vkCode;
                if (vk > 0 && vk < 256 && !IsModifier(vk))
                {
                    if (!down)
                    {
                        // Only a key-up whose key-down we ate is eaten: anything else the game saw go down must see come up.
                        if (s_swallowed[vk]) { s_swallowed[vk] = false; return (LRESULT)1; }
                    }
                    else
                    {
                        // A held key whose first down we ate keeps being eaten while it auto-repeats (even if a modifier was let
                        // go meanwhile): the game never saw it go down, so it must not see a repeat either. Nothing re-fires.
                        if (s_swallowed[vk]) return (LRESULT)1;
                        // Live modifier state: the modifiers' own events were processed before this one arrived.
                        bool ctrl = IsDown(VK_CONTROL), alt = IsDown(VK_MENU), shift = IsDown(VK_SHIFT), win = IsDown(VK_LWIN) || IsDown(VK_RWIN);
                        var capture = s_captureNext;
                        if (capture != null)
                        {
                            if (!IsReservedForCapture(vk, ctrl, alt, shift, win))
                            {
                                s_captureNext = null;
                                s_swallowed[vk] = true;
                                capture(vk, ctrl, alt, shift, win);
                                return (LRESULT)1;
                            }
                        }
                        else
                        {
                            var match = Match(vk, ctrl, alt, shift, win);
                            if (match != null)
                            {
                                s_swallowed[vk] = true;
                                s_onPressed?.Invoke(match);
                                return (LRESULT)1; // swallow so the game doesn't see it
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return PInvoke.CallNextHookEx(s_hook, code, wParam, lParam);
    }

    private static HotkeyAction? Match(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        var actions = s_actions;
        for (int i = 0; i < actions.Length; i++)
        {
            var k = actions[i].Keys;
            if (vk == k.Vk && ctrl == k.Ctrl && alt == k.Alt && shift == k.Shift && win == k.Win) return actions[i];
        }
        return null;
    }

    public static string KeyName(int vk)
    {
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        switch (vk)
        {
            case 0x20: return "Space"; case 0x2D: return "Insert"; case 0x2E: return "Delete"; case 0x24: return "Home"; case 0x23: return "End";
            case 0x21: return "PageUp"; case 0x22: return "PageDown"; case 0x2C: return "PrintScreen"; case 0x91: return "ScrollLock"; case 0x13: return "Pause";
            case 0x60: return "Num0"; case 0x61: return "Num1"; case 0x62: return "Num2"; case 0x63: return "Num3"; case 0x64: return "Num4";
            case 0x65: return "Num5"; case 0x66: return "Num6"; case 0x67: return "Num7"; case 0x68: return "Num8"; case 0x69: return "Num9";
            case 0x6A: return "Num*"; case 0x6B: return "Num+"; case 0x6D: return "Num-"; case 0x6E: return "Num."; case 0x6F: return "Num/";
            case 0xC0: return "`"; case 0xBD: return "-"; case 0xBB: return "="; case 0xDB: return "["; case 0xDD: return "]"; case 0xDC: return "\\";
            case 0xBA: return ";"; case 0xDE: return "'"; case 0xBC: return ","; case 0xBE: return "."; case 0xBF: return "/";
            case 0x08: return "Backspace"; case 0x09: return "Tab"; case 0x0D: return "Enter"; case 0x1B: return "Esc";
            case 0x25: return "Left"; case 0x26: return "Up"; case 0x27: return "Right"; case 0x28: return "Down";
            case 0x14: return "CapsLock"; case 0x90: return "NumLock";
        }
        try
        {
            uint scan = PInvoke.MapVirtualKey((uint)vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
            Span<char> buf = stackalloc char[64];
            fixed (char* p = buf)
            {
                int n = PInvoke.GetKeyNameText((int)(scan << 16), new PWSTR(p), 64);
                if (n > 0) return new string(p, 0, n);
            }
        }
        catch { }
        return "Key" + vk;
    }
}
