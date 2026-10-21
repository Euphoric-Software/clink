using System.IO;
using Clink.Engine;
using Clink.Native;
using Clink.Services;

namespace Clink;

/// <summary>`Clink.exe --selftest [seconds]`: runs the pipeline headless, saves a clip, prints what happened.</summary>
internal static class SelfTest
{
    private static void Say(string s) { Console.WriteLine(s); Log.Info("[selftest] " + s); }

    public static int Run(string[] args)
    {
        int seconds = 8;
        if (args.Length > 1 && int.TryParse(args[1], out int s)) seconds = Math.Clamp(s, 2, 600);
        var settings = Settings.Load();
        Say($"Clink self-test ({seconds}s). Log: {Log.Path}");

        var monitors = Monitors.Enumerate();
        foreach (var m in monitors)
            Say($"  monitor {m.ShortName,-9} {m.FriendlyName,-12} {m.Width}x{m.Height} @({m.Left},{m.Top}) primary={m.IsPrimary} hdr={m.IsHdr} sdrWhite={m.SdrWhiteNits:0}nits maxLum={m.MaxLuminance:0} gpu={m.AdapterName}");
        var mon = monitors.FirstOrDefault(m => m.DeviceName == settings.MonitorDevice) ?? monitors.First(m => m.IsPrimary);
        Say($"Capturing {mon.Label}");

        var cfg = new EngineConfig
        {
            Monitor = mon,
            OutWidth = (uint)(mon.Width & ~1),
            OutHeight = (uint)(mon.Height & ~1),
            Fps = (uint)settings.Fps,
            BitrateMbps = (uint)settings.BitrateMbps,
            Hevc = settings.Codec == "hevc",
            BufferSeconds = Math.Max(10, Math.Min(settings.BufferSeconds, 60)),
            CaptureCursor = true,
            TrackAll = settings.TrackAll, TrackGame = settings.TrackGame, TrackMic = settings.TrackMic, TrackVoice = settings.TrackVoice,
            MicDeviceId = settings.MicDeviceId,
            VoiceApp = settings.VoiceApp,
            ClipsFolder = settings.ClipsFolder,
            GopSeconds = settings.KeyframeSeconds,
        };

        using var engine = new Engine.Engine();
        engine.Start(cfg);
        if (!engine.IsRunning)
        {
            Say("ENGINE FAILED: " + engine.Error);
            return 2;
        }
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var st = engine.GetStats();
            string audio = string.Join(", ", st.Audio.Select(a => $"{a.Name}:{(a.Running ? "ok" : "off")}"));
            Say($"  t={i + 1}s capture={st.CaptureFrames} in={st.FramesIn} out={st.FramesOut} dropped={st.Dropped} buffered={st.BufferedSeconds:0.0}s game={st.Game ?? "-"} audio=[{audio}]");
        }
        try
        {
            var clip = engine.SaveClip(seconds);
            Say($"Saved {clip.Path} ({clip.DurationSeconds:0.00}s, {clip.SizeBytes / 1_048_576.0:0.0} MB)");
            ClickSound.Play(settings.ClickVolume);
            Thread.Sleep(300);
            return 0;
        }
        catch (Exception ex)
        {
            Say("SAVE FAILED: " + ex);
            return 3;
        }
    }
}
