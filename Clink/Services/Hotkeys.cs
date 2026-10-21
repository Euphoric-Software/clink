using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Clink.Services;

/// <summary>Global low-level keyboard hook (works while games have focus, including injected keys from Stream Deck).</summary>
internal static unsafe class Hotkeys
{
    private static HHOOK s_hook;
    private static HotkeyBinding? s_binding;
    private static Action? s_onPressed;
    private static bool s_ctrl, s_alt, s_shift, s_win;
    private static bool s_down;
    private static Action<int, bool, bool, bool, bool>? s_captureNext;

    public static void Install(HotkeyBinding binding, Action onPressed)
    {
        s_binding = binding;
        s_onPressed = onPressed;
        if (s_hook.IsNull)
        {
            s_hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, default, 0);
            if (s_hook.IsNull) Log.Warn("Keyboard hook failed: " + Marshal.GetLastWin32Error());
        }
    }

    public static void Rebind(HotkeyBinding binding) => s_binding = binding;

    /// <summary>The next non-modifier key press is reported (with modifiers) and swallowed; used by the "change hotkey" UI.</summary>
    public static void CaptureNext(Action<int, bool, bool, bool, bool>? handler) => s_captureNext = handler;

    public static void Uninstall()
    {
        if (!s_hook.IsNull) { PInvoke.UnhookWindowsHookEx(s_hook); s_hook = default; }
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
                switch (vk)
                {
                    case 0xA2: case 0xA3: case 0x11: s_ctrl = down; break;
                    case 0xA4: case 0xA5: case 0x12: s_alt = down; break;
                    case 0xA0: case 0xA1: case 0x10: s_shift = down; break;
                    case 0x5B: case 0x5C: s_win = down; break;
                    default:
                        if (s_captureNext != null)
                        {
                            if (down)
                            {
                                var h = s_captureNext; s_captureNext = null;
                                h(vk, s_ctrl, s_alt, s_shift, s_win);
                            }
                            return (LRESULT)1;
                        }
                        var b = s_binding;
                        if (b != null && vk == b.Vk && s_ctrl == b.Ctrl && s_alt == b.Alt && s_shift == b.Shift && s_win == b.Win)
                        {
                            if (down && !s_down) { s_down = true; s_onPressed?.Invoke(); }
                            else if (!down) s_down = false;
                            return (LRESULT)1; // swallow so the game doesn't see it
                        }
                        if (!down && vk == b?.Vk) s_down = false;
                        break;
                }
            }
            catch { }
        }
        return PInvoke.CallNextHookEx(s_hook, code, wParam, lParam);
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
