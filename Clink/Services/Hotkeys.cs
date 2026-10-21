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
/// The hook runs on a thread of its own: Windows holds every keystroke on the system until the hook returns, so it must never
/// wait for the UI thread (a clipboard retry, a GC, the first toast being built). A hook that once took longer than
/// LowLevelHooksTimeout is dropped by Windows without a word, so it is put back after unlock / resume and once a minute.
/// </summary>
internal static unsafe class Hotkeys
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SPACE = 0x20, VK_ESCAPE = 0x1B, VK_F4 = 0x73, VK_DELETE = 0x2E;
    private const int VK_CANCEL = 0x03, VK_PAUSE = 0x13, VK_SCROLL = 0x91;
    private const uint WM_QUIT = 0x0012, WM_REHOOK = 0x8000 + 0x4B;   // WM_APP + n to the hook thread: re-install (wParam 1: forget held keys too)

    private static HHOOK s_hook;                 // hook thread only
    private static Thread? s_thread;
    private static uint s_threadId;
    private static System.Threading.Timer? s_rehookTimer;
    private static HotkeyAction[] s_actions = Array.Empty<HotkeyAction>();
    private static Action<HotkeyAction>? s_onPressed;
    private static readonly bool[] s_swallowed = new bool[256];   // keys whose key-down we ate (binding fired, or capture): their key-up is eaten too, auto-repeat does not re-fire
    private static Capture? s_capture;
    private static bool s_sessionHooked;

    private sealed class Capture
    {
        public required Action<int, bool, bool, bool, bool> OnKeys;
        public Action? OnRefused;
    }

    /// <summary>The bindings the hook currently matches (snapshot).</summary>
    public static IReadOnlyList<HotkeyAction> Actions => s_actions;

    /// <summary>Installs the hook (once) and sets the bindings. <paramref name="onPressed"/> runs on the hook thread: marshal to the UI, never block.</summary>
    public static void Install(IReadOnlyList<HotkeyAction> actions, Action<HotkeyAction> onPressed)
    {
        Rebind(actions);
        s_onPressed = onPressed;
        if (s_thread == null)
        {
            var ready = new ManualResetEventSlim();
            s_thread = new Thread(() => HookThread(ready)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Clink hotkeys" };
            s_thread.Start();
            if (!ready.Wait(3000)) Log.Warn("hotkeys: the hook thread did not start");
            s_rehookTimer = new System.Threading.Timer(_ => Post(reset: false), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        if (!s_sessionHooked)
        {
            try
            {
                // Lock / unlock / switch user: whatever was held when the desktop changed is not held any more as far as we know.
                // After unlock and resume the hook is also put back: a slow first key while pages are cold may have cost it.
                SystemEvents.SessionSwitch += (_, _) => Post(reset: true);
                SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Resume) Post(reset: true); };
                s_sessionHooked = true;
            }
            catch (Exception ex) { Log.Warn("hotkeys: session events: " + ex.Message); }
        }
    }

    /// <summary>Forgets every half-seen key (after a desktop switch nothing we tracked can be trusted).</summary>
    public static void Reset() => Array.Clear(s_swallowed);

    /// <summary>Replaces the bindings (after the list in settings changed). Entries without a key are skipped.</summary>
    public static void Rebind(IReadOnlyList<HotkeyAction> actions)
    {
        var list = actions.Where(a => a?.Keys != null && a.Keys.Vk > 0).ToArray();
        // Only the first binding of a combo ever fires (Match): say so for a list with repeats (a hand-edited settings file).
        foreach (var g in list.GroupBy(a => (a.Keys.Vk, a.Keys.Ctrl, a.Keys.Alt, a.Keys.Shift, a.Keys.Win)).Where(g => g.Count() > 1))
            Log.Warn($"hotkeys: {g.First().Keys} is bound {g.Count()} times; only the first ({g.First().Action}) fires");
        s_actions = list;
    }

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
    /// system owns (Alt+Tab, Alt+Esc, Alt+F4, Ctrl+Esc, Ctrl+Shift+Esc, any Win chord), a plain Enter / Space / Tab and a bare
    /// typing key (letter, digit, punctuation, Backspace, Delete, arrow: it would be eaten in every program) are passed through
    /// untouched and leave the capture armed; a typing key also calls <paramref name="refused"/>. Esc is reported (the UI
    /// cancels on it). Both callbacks run on the hook thread: marshal to the UI, never block.
    /// </summary>
    public static void CaptureNext(Action<int, bool, bool, bool, bool>? handler, Action? refused = null) =>
        s_capture = handler == null ? null : new Capture { OnKeys = handler, OnRefused = refused };

    public static void Uninstall()
    {
        s_rehookTimer?.Dispose();
        s_rehookTimer = null;
        var t = s_thread;
        if (t != null)
        {
            PostThreadMessage(s_threadId, WM_QUIT, 0, 0);   // the thread unhooks on its way out
            if (!t.Join(1000)) Log.Warn("hotkeys: the hook thread did not stop");
            s_thread = null;
            s_threadId = 0;
        }
        Reset();
    }

    // ------------------------------------------------------------------ hook thread

    private static void HookThread(ManualResetEventSlim ready)
    {
        s_threadId = GetCurrentThreadId();
        PeekMessage(out _, 0, 0, 0, 0);   // PM_NOREMOVE: creates this thread's message queue, so PostThreadMessage works from here on
        Hook(reset: false);
        ready.Set();
        // The hook procedure is called from inside GetMessage; the only messages that come out are ours.
        while (GetMessage(out var msg, 0, 0, 0) > 0)
            if (msg.message == WM_REHOOK) Hook(reset: msg.wParam != 0);
        if (!s_hook.IsNull) { PInvoke.UnhookWindowsHookEx(s_hook); s_hook = default; }
    }

    /// <summary>Hook thread: puts a fresh hook in, then takes the old one out (no moment without one, and no key arrives in between).</summary>
    private static void Hook(bool reset)
    {
        if (reset) Reset();
        var old = s_hook;
        var fresh = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, &HookProc, default, 0);
        if (fresh.IsNull)
        {
            Log.Warn("Keyboard hook failed: " + Marshal.GetLastWin32Error() + (old.IsNull ? "" : " (the previous one stays)"));
            return;
        }
        s_hook = fresh;
        if (!old.IsNull && old != fresh && !PInvoke.UnhookWindowsHookEx(old))
            Log.Warn("hotkeys: Windows had removed the keyboard hook (a key once took too long to answer); it is back now");
    }

    private static void Post(bool reset)
    {
        uint id = s_threadId;
        if (id != 0) PostThreadMessage(id, WM_REHOOK, reset ? 1 : 0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; public uint lPrivate; }

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg msg, nint hwnd, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static extern int PeekMessage(out Msg msg, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    private static extern int PostThreadMessage(uint thread, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ------------------------------------------------------------------ matching

    private static bool IsModifier(int vk) => vk is 0xA0 or 0xA1 or VK_SHIFT or 0xA2 or 0xA3 or VK_CONTROL or 0xA4 or 0xA5 or VK_MENU or VK_LWIN or VK_RWIN;

    private static bool IsDown(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>A key that types or edits text when pressed without Ctrl / Alt (Shift only picks the character).</summary>
    private static bool IsTypingKey(int vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE or 0x08 or VK_DELETE or >= 0x25 and <= 0x28;

    /// <summary>A key the capture must not take: it belongs to the shell / window manager, or it is a bare editing or typing key.</summary>
    private static bool IsReservedForCapture(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        if (win) return true;
        if (alt && vk is VK_TAB or VK_ESCAPE or VK_F4) return true;
        if (ctrl && vk == VK_ESCAPE) return true;
        if (ctrl && alt && vk == VK_DELETE) return true;
        if (!ctrl && !alt && !shift && vk is VK_TAB or VK_RETURN or VK_SPACE) return true;
        if (!ctrl && !alt && IsTypingKey(vk)) return true;
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
                int vk = (int)ks->vkCode, raw = vk;
                // Ctrl turns ScrollLock (scan 46) and Pause (E0 46) into VK_CANCEL. Handled as the key itself: a ScrollLock binding
                // still fires while crouch (Ctrl) is held, and a key-up that comes after Ctrl was let go still finds its key-down.
                if (vk == VK_CANCEL && ks->scanCode == 0x46) vk = (ks->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_EXTENDED) != 0 ? VK_PAUSE : VK_SCROLL;
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
                        var capture = s_capture;
                        if (capture != null)
                        {
                            if (!IsReservedForCapture(vk, ctrl, alt, shift, win))
                            {
                                // The UI may cancel or replace the capture right now: only the one we read is answered.
                                if (Interlocked.CompareExchange(ref s_capture, null, capture) == capture)
                                {
                                    s_swallowed[vk] = true;
                                    capture.OnKeys(vk, ctrl, alt, shift, win);
                                    return (LRESULT)1;
                                }
                            }
                            else if (!ctrl && !alt && !win && IsTypingKey(vk)) capture.OnRefused?.Invoke();
                        }
                        else
                        {
                            var match = Match(vk, raw, ctrl, alt, shift, win);
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

    /// <summary>
    /// The binding for a key press: the exact combo first (F9 and Shift+F9 may do different things), then a binding on that key
    /// with fewer modifiers, the most of them held winning: a plain ScrollLock still clips while crouch (Ctrl) or sprint (Shift)
    /// is held. That fallback never takes a Win chord or a combo the system owns (Alt+F4 with F4 bound). <paramref name="raw"/>
    /// is the key code as reported, for a binding saved as Ctrl+Break before ScrollLock / Pause were told apart.
    /// </summary>
    private static HotkeyAction? Match(int vk, int raw, bool ctrl, bool alt, bool shift, bool win)
    {
        var actions = s_actions;
        for (int i = 0; i < actions.Length; i++)
        {
            var k = actions[i].Keys;
            if ((vk == k.Vk || raw == k.Vk) && ctrl == k.Ctrl && alt == k.Alt && shift == k.Shift && win == k.Win) return actions[i];
        }
        if (IsReservedForCapture(vk, ctrl, alt, shift, win)) return null;
        HotkeyAction? best = null;
        int bestMods = -1;
        for (int i = 0; i < actions.Length; i++)
        {
            var k = actions[i].Keys;
            if (vk != k.Vk || win != k.Win || (k.Ctrl && !ctrl) || (k.Alt && !alt) || (k.Shift && !shift)) continue;
            int mods = (k.Ctrl ? 1 : 0) + (k.Alt ? 1 : 0) + (k.Shift ? 1 : 0);
            if (mods > bestMods) { best = actions[i]; bestMods = mods; }
        }
        return best;
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
            // keys whose scan code is shared with a letter (the E0 prefix tells them apart), or that have no key name at all
            case 0x03: return "Break"; case 0x5D: return "Menu";
            case 0xA6: return "Browser Back"; case 0xA7: return "Browser Forward"; case 0xA8: return "Browser Refresh"; case 0xA9: return "Browser Stop";
            case 0xAA: return "Browser Search"; case 0xAB: return "Browser Favorites"; case 0xAC: return "Browser Home";
            case 0xAD: return "Mute"; case 0xAE: return "Volume Down"; case 0xAF: return "Volume Up";
            case 0xB0: return "Next Track"; case 0xB1: return "Previous Track"; case 0xB2: return "Stop Media"; case 0xB3: return "Play/Pause";
            case 0xB4: return "Mail"; case 0xB5: return "Media Select"; case 0xB6: return "App1"; case 0xB7: return "App2";
        }
        try
        {
            // The _EX scan code keeps the E0 prefix: without the extended bit an E0 key is named after the key sharing its base code.
            uint scan = PInvoke.MapVirtualKey((uint)vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX);
            int lparam = (int)((scan & 0xFF) << 16) | ((scan & 0xFF00) == 0xE000 ? 1 << 24 : 0);
            Span<char> buf = stackalloc char[64];
            fixed (char* p = buf)
            {
                int n = PInvoke.GetKeyNameText(lparam, new PWSTR(p), 64);
                if (n > 0) return new string(p, 0, n);
            }
        }
        catch { }
        return "Key" + vk;
    }
}
