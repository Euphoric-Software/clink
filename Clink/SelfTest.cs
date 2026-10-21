using System.IO;
using Clink.Engine;
using Clink.Native;
using Clink.Services;

namespace Clink;

/// <summary>
/// `Clink.exe --selftest [seconds] [outFolder] [--codec h264|hevc|av1] [--quiet]`: runs the pipeline headless, saves a clip,
/// prints what happened. The clip goes to outFolder (default: the clips folder from settings); --codec overrides the codec
/// setting; --quiet skips the click sound.
/// </summary>
internal static class SelfTest
{
    private static void Say(string s) { Console.WriteLine(s); Log.Info("[selftest] " + s); }

    public static int Run(string[] args)
    {
        int seconds = 8;
        if (args.Length > 1 && int.TryParse(args[1], out int s)) seconds = Math.Clamp(s, 2, 600);
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        string? codecArg = null, outDir = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i].Equals("--codec", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) codecArg = args[++i];
            else if (!args[i].StartsWith("--", StringComparison.Ordinal)) outDir ??= args[i];
        }
        var settings = Settings.Load();
        if (codecArg != null) settings.Codec = codecArg;
        if (outDir != null) { outDir = Path.GetFullPath(outDir); Directory.CreateDirectory(outDir); }
        Say($"Clink self-test ({seconds}s). Log: {Log.Path}");

        var monitors = Monitors.Enumerate();
        foreach (var m in monitors)
            Say($"  monitor {m.ShortName,-9} {m.FriendlyName,-12} {m.Width}x{m.Height} @({m.Left},{m.Top}) primary={m.IsPrimary} hdr={m.IsHdr} sdrWhite={m.SdrWhiteNits:0}nits maxLum={m.MaxLuminance:0} gpu={m.AdapterName}");
        var mon = monitors.FirstOrDefault(m => m.DeviceName == settings.MonitorDevice) ?? monitors.First(m => m.IsPrimary);
        Say($"Capturing {mon.Label}");
        Capabilities.PreferMonitor(mon);   // probe the encoders of the GPU that drives it, which is the one the engine opens
        uint outWidth = (uint)(mon.Width & ~1), outHeight = (uint)(mon.Height & ~1);

        foreach (var c in Capabilities.Codecs)
            Say($"  codec {c.Name,-5} {(c.Supported ? c.EncoderName : c.Reason)}{(c.Experimental && c.Supported ? " (experimental)" : "")}");
        Say($"  RAM {Capabilities.FormatBytes(Capabilities.PhysicalRamBytes())}, ring limit {Capabilities.FormatBytes(Capabilities.MaxRingBytes())}");
        string codec = Capabilities.EffectiveCodec(settings.Codec, outWidth, outHeight);
        if (codec != EngineConfig.NormalizeCodec(settings.Codec))
            Say(Capabilities.IsSupported(settings.Codec) ? $"  H.264 cannot encode {outWidth}x{outHeight}, using {codec}" : $"  {settings.Codec} has no hardware encoder here, using {codec}");
        int audioTracks = 1 + (settings.TrackGame ? 1 : 0) + (settings.TrackMic ? 1 : 0) + (settings.TrackVoice ? 1 : 0) + (settings.TrackDesktop ? 1 : 0);
        var (bitrate, note) = Capabilities.ResolveBitrate(settings, audioTracks, settings.BufferSeconds);
        Say($"  bitrate {bitrate:0.#} Mbps ({codec}){(note != null ? ": " + note : "")}; {Capabilities.DescribeEstimate(bitrate, audioTracks, settings.BufferSeconds)}; {Capabilities.DescribeRing(bitrate, settings.BufferSeconds).text}");

        var cfg = new EngineConfig
        {
            Monitor = mon,
            OutWidth = outWidth,
            OutHeight = outHeight,
            Fps = (uint)settings.Fps,
            BitrateMbps = bitrate,
            Codec = codec,
            BufferSeconds = Math.Max(10, Math.Min(settings.BufferSeconds, 60)),
            CaptureCursor = true,
            TrackDesktop = settings.TrackDesktop, TrackGame = settings.TrackGame, TrackMic = settings.TrackMic, TrackVoice = settings.TrackVoice,
            MicDeviceId = settings.MicDeviceId,
            VoiceApp = settings.VoiceApp,
            ClipsFolder = outDir ?? settings.ClipsFolder,
            GopSeconds = settings.KeyframeSeconds,
        };

        using var engine = new Engine.Engine();
        engine.Start(cfg);
        if (!engine.IsRunning)
        {
            Say("ENGINE FAILED: " + engine.Error);
            return 2;
        }
        {
            var st = engine.GetStats();
            Say($"  encoder {st.Encoder} ({st.Codec}), ring {Capabilities.FormatBytes(st.RingBytes)} = {st.RingSeconds}s");
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
            if (!quiet) { ClickSound.Play(settings.ClickVolume); Thread.Sleep(300); }
            return 0;
        }
        catch (Exception ex)
        {
            Say("SAVE FAILED: " + ex);
            return 3;
        }
    }
}
