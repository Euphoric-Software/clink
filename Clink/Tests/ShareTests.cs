using System.IO;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using Clink.Services;
using Clink.UI;

namespace Clink.Tests;

/// <summary>Share: the pre-check against Copium's limits for the account tier, its footer line, and the file the share window drags and copies.</summary>
internal static class ShareTests
{
    private const long MB = 1_000_000;

    [UnitTest]
    public static void A_clip_just_under_the_free_limits_is_not_refused()
    {
        Check.Null(AppController.ShareRefusalFor(199 * MB, 9 * 60 + 59, plus: false), "199 MB / 9:59");
        Check.Null(AppController.ShareRefusalFor(1, 1, plus: false), "tiny");
        // Exactly at a limit still goes: Copium refuses what is over, and a full 10:00 replay is 600 s.
        Check.Null(AppController.ShareRefusalFor(200 * MB, 600, plus: false), "200 MB / 10:00");
    }

    [UnitTest]
    public static void A_clip_over_the_free_size_or_length_is_refused_with_the_free_limit()
    {
        const string free = "Over Copium's free limit (200 MB / 10 min)";
        Check.Equal(free, AppController.ShareRefusalFor(201 * MB, 30, plus: false), "201 MB");
        Check.Equal(free, AppController.ShareRefusalFor(50 * MB, 601, plus: false), "10:01");
        // A clip found in the folder has no duration: only its size can refuse it.
        Check.Null(AppController.ShareRefusalFor(50 * MB, 0, plus: false), "50 MB, no duration");
        Check.Equal(free, AppController.ShareRefusalFor(201 * MB, 0, plus: false), "201 MB, no duration");
    }

    [UnitTest]
    public static void With_Copium_Plus_the_limits_are_two_gigabytes_and_an_hour()
    {
        const string plus = "Over Copium Plus's limit (2 GB / 60 min)";
        Check.Null(AppController.ShareRefusalFor(1_900 * MB, 600, plus: true), "1.9 GB");
        Check.Null(AppController.ShareRefusalFor(2_000 * MB, 3600, plus: true), "exactly 2 GB / 60 min");
        Check.Equal(plus, AppController.ShareRefusalFor(2_001 * MB, 600, plus: true), "2.001 GB");
        Check.Equal(plus, AppController.ShareRefusalFor(100 * MB, 61 * 60, plus: true), "61 min");
        // What the free account turns down, Plus takes.
        Check.NotNull(AppController.ShareRefusalFor(1_900 * MB, 600, plus: false), "1.9 GB without Plus");
        Check.NotNull(AppController.ShareRefusalFor(100 * MB, 30 * 60, plus: false), "30 min without Plus");
        Check.Null(AppController.ShareRefusalFor(100 * MB, 30 * 60, plus: true), "30 min with Plus");
    }

    [UnitTest]
    public static void The_refusal_is_Over_and_the_label_of_the_tier()
    {
        Check.Equal("Copium's free limit (200 MB / 10 min)", Capabilities.CopiumLimits(false).Label);
        Check.Equal("Copium Plus's limit (2 GB / 60 min)", Capabilities.CopiumLimits(true).Label);
        foreach (bool plus in new[] { false, true })
        {
            var (bytes, seconds, label) = Capabilities.CopiumLimits(plus);
            Check.Equal("Over " + label, AppController.ShareRefusalFor(bytes + 1, 10, plus), $"one byte over, plus={plus}");
            Check.Equal("Over " + label, AppController.ShareRefusalFor(1, seconds + 1, plus), $"a second over, plus={plus}");
            Check.Null(AppController.ShareRefusalFor(bytes, seconds, plus), $"at the limits, plus={plus}");
        }
    }

    [UnitTest]
    public static void The_footer_line_says_what_the_account_takes_and_what_to_do()
    {
        // 602.4 s is rounded up, like a full 10:00 replay that saves a moment over 10 min: it must not read 10:00
        Check.Equal("Copium free accepts up to 200 MB / 10 min; this clip is 211 MB / 10:03 - trim it, or tick 'I have Copium+' if you do",
            AppController.ShareRefusalNotice(211 * MB, 602.4, plus: false));
        // a clip found in the folder has no duration to show
        Check.Equal("Copium free accepts up to 200 MB / 10 min; this clip is 250 MB - trim it, or tick 'I have Copium+' if you do",
            AppController.ShareRefusalNotice(250 * MB, 0, plus: false));
        // with Plus ticked there is nothing left to tick
        Check.Equal("Copium Plus accepts up to 2 GB / 60 min; this clip is 3 GB / 61:40 - trim it first",
            AppController.ShareRefusalNotice(3_000 * MB, 3700, plus: true));
    }

    [UnitTest]
    public static void The_dragged_and_pasted_file_may_only_be_copied()
    {
        string path = @"C:\Clips\Overwatch 2026-10-20 21-04-11.mp4";
        var data = ShareWindow.FileData(path);

        Check.True(data.GetDataPresent(DataFormats.FileDrop), "a file drop is what Explorer, Discord and upload boxes take");
        Check.Equal(new[] { path }, data.GetData(DataFormats.FileDrop) as string[]);

        // "Preferred DropEffect" is a DWORD: 1 = copy. 2 (move) would let Explorer move the clip out of the library.
        Check.Equal("Preferred DropEffect", ShareWindow.PreferredDropEffect);
        Check.True(data.GetDataPresent(ShareWindow.PreferredDropEffect), "Preferred DropEffect");
        var stream = data.GetData(ShareWindow.PreferredDropEffect) as Stream;
        Check.NotNull(stream, "the effect is a stream of 4 bytes");
        using var bytes = new MemoryStream();
        stream!.Position = 0;
        stream.CopyTo(bytes);
        Check.Equal(new byte[] { 1, 0, 0, 0 }, bytes.ToArray());
        Check.Equal(1, (int)DragDropEffects.Copy);
    }

    [UnitTest]
    public static void The_file_data_reaches_a_drop_target_as_a_file_list_and_a_copy_effect()
    {
        // What OLE asks of the data object when it is dragged or put on the clipboard (OleFlushClipboard does the same for every
        // format it lists), without touching the real clipboard: every listed format renders, a file list names the file, and the
        // effect is a DWORD 1 = copy.
        string path = @"C:\Clips\Overwatch 2026-10-20 21-04-11.mp4";
        var ole = (System.Runtime.InteropServices.ComTypes.IDataObject)ShareWindow.FileData(path);

        int listed = 0;
        var formats = ole.EnumFormatEtc(DATADIR.DATADIR_GET);
        var fe = new FORMATETC[1];
        var fetched = new int[1];
        while (formats.Next(1, fe, fetched) == 0)
        {
            listed++;
            var asked = fe[0];
            if ((asked.tymed & TYMED.TYMED_HGLOBAL) == 0) continue;
            Check.True(Render(ole, asked.cfFormat).Length > 0, $"format {asked.cfFormat} renders");
        }
        Check.True(listed >= 2, "a file list and the effect are offered");

        var drop = Render(ole, (short)DataFormats.GetDataFormat(DataFormats.FileDrop).Id);
        int listAt = BitConverter.ToInt32(drop, 0), wide = BitConverter.ToInt32(drop, 16);   // DROPFILES: pFiles, ..., fWide
        Check.Equal(1, wide, "the list is UTF-16");
        Check.Equal(path, System.Text.Encoding.Unicode.GetString(drop, listAt, drop.Length - listAt).Split('\0')[0]);

        var effect = Render(ole, (short)DataFormats.GetDataFormat(ShareWindow.PreferredDropEffect).Id);
        Check.Equal(1, BitConverter.ToInt32(effect, 0), "DROPEFFECT_COPY");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr h);
    [System.Runtime.InteropServices.DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM m);

    /// <summary>The bytes of one format of a data object, as a drop target or the clipboard reads them (an HGLOBAL).</summary>
    private static byte[] Render(System.Runtime.InteropServices.ComTypes.IDataObject ole, short format)
    {
        var ask = new FORMATETC { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, ptd = IntPtr.Zero, tymed = TYMED.TYMED_HGLOBAL };
        ole.GetData(ref ask, out STGMEDIUM medium);
        try
        {
            IntPtr h = medium.unionmember;
            var bytes = new byte[(int)GlobalSize(h)];
            IntPtr p = GlobalLock(h);
            try { System.Runtime.InteropServices.Marshal.Copy(p, bytes, 0, bytes.Length); }
            finally { GlobalUnlock(h); }
            return bytes;
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    [UnitTest]
    public static void The_drag_allows_copy_and_nothing_else()
    {
        // The one place the allowed effects are named: the drag uses it, and the data's "Preferred DropEffect" is built from it. Adding
        // Move (or Link) here is what would let Explorer take the clip out of the library.
        Check.Equal(DragDropEffects.Copy, ShareWindow.DragEffects);
        Check.Equal(0, (int)(ShareWindow.DragEffects & DragDropEffects.Move), "no Move");
        Check.Equal(0, (int)(ShareWindow.DragEffects & DragDropEffects.Link), "no Link");
        Check.Equal(0, (int)(ShareWindow.DragEffects & DragDropEffects.Scroll), "no Scroll");

        var stream = (Stream)ShareWindow.FileData(@"C:\Clips\a.mp4").GetData(ShareWindow.PreferredDropEffect)!;
        using var bytes = new MemoryStream();
        stream.Position = 0;
        stream.CopyTo(bytes);
        Check.Equal(BitConverter.GetBytes((int)ShareWindow.DragEffects), bytes.ToArray(), "the data says what the drag allows");
    }

    [UnitTest]
    public static void The_windows_kept_out_of_captures_are_marked_so_their_tooltips_are_too()
    {
        // The tooltip handler (CaptureExclusion) matches the marker, not a window class: a window that hides itself from captures
        // without it would show its tooltips to the recorder.
        Check.True(typeof(ICaptureExcludedWindow).IsAssignableFrom(typeof(ShareWindow)), "the share window");
        Check.True(typeof(ICaptureExcludedWindow).IsAssignableFrom(typeof(ToastWindow)), "the toasts");
        CaptureExclusion.Register();   // idempotent: every such class calls it from its static constructor
        CaptureExclusion.Register();
    }

    [UnitTest]
    public static void A_full_screen_application_counts_as_the_game_in_front()
    {
        // QUERY_USER_NOTIFICATION_STATE: 1 not present, 2 busy, 3 running D3D full screen, 4 presentation mode, 5 accepts
        // notifications, 6 quiet time, 7 app
        foreach (int busy in new[] { 2, 3, 4 }) Check.True(UserNotificationState.IsFullScreenLike(busy), $"state {busy}");
        foreach (int free in new[] { 0, 1, 5, 6, 7, 8 }) Check.False(UserNotificationState.IsFullScreenLike(free), $"state {free}");
    }

    [UnitTest]
    public static void Over_a_game_the_window_opens_where_the_toasts_do()
    {
        // a 2560x1440 work area with a 40 px taskbar, a 480x283 window, a 16 px margin
        (int x, int y) At(string? position) => ShareWindow.ToastCorner(0, 0, 2560, 1400, position, 480, 283, 16);
        Check.Equal((2064, 1101), At("bottom-right"));
        Check.Equal((2064, 1101), At(null));            // nothing set: the toasts' default
        Check.Equal((16, 1101), At("bottom-left"));
        Check.Equal((2064, 16), At("top-right"));
        Check.Equal((16, 16), At("top-left"));
        Check.Equal((1040, 16), At("top"));              // a side the name does not give is the middle
        Check.Equal((1040, 1101), At("bottom"));
        Check.Equal((16, 558), At("left"));
        Check.Equal((2064, 558), At("right"));
        Check.Equal((2064, 1101), At("  Bottom-Right "));
        // another monitor: the work area's own origin counts
        Check.Equal((-1920 + 16, 215 + 16), ShareWindow.ToastCorner(-1920, 215, 0, 1255, "top-left", 480, 283, 16));
    }

    [UnitTest]
    public static void The_card_shows_size_then_length_when_it_is_known()
    {
        Check.Equal("190 MB · 1:30", ShareWindow.MetaFor(190 * MB, 90));
        Check.Equal("190 MB", ShareWindow.MetaFor(190 * MB, 0));   // found in the folder: no length
        Check.Equal("1:30", ShareWindow.MetaFor(0, 90));
        Check.Equal("", ShareWindow.MetaFor(0, 0));
    }
}
