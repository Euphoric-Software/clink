using System.IO;
using Clink.Audio;
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
        int seconds = 8, rest = 1; // rest: first argument after the optional length
        if (args.Length > 1 && int.TryParse(args[1], out int s)) { seconds = Math.Clamp(s, 2, 600); rest = 2; }
        bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
        string? codecArg = null, outDir = null;
        for (int i = rest; i < args.Length; i++)
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
        var mon = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, settings.MonitorDevice, StringComparison.OrdinalIgnoreCase))
            ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (mon == null) { Say("No monitor found"); return 2; }
        Say($"Capturing {mon.Label}");

        foreach (var c in Capabilities.Codecs)
            Say($"  codec {c.Name,-5} {(c.Supported ? c.EncoderName : c.Reason)}{(c.Experimental && c.Supported ? " (experimental)" : "")}");
        Say($"  RAM {Capabilities.FormatBytes(Capabilities.PhysicalRamBytes())}, ring limit {Capabilities.FormatBytes(Capabilities.MaxRingBytes())}");
        string codec = Capabilities.EffectiveCodec(settings.Codec);
        if (codec != EngineConfig.NormalizeCodec(settings.Codec)) Say($"  {settings.Codec} has no hardware encoder here, using {codec}");
        int audioTracks = 1 + (settings.TrackGame ? 1 : 0) + (settings.TrackMic ? 1 : 0) + (settings.TrackVoice ? 1 : 0) + (settings.TrackDesktop ? 1 : 0);
        int secs = Math.Clamp(settings.BufferSeconds, 5, 3600);
        var (bitrate, note) = Capabilities.ResolveBitrate(settings, audioTracks, secs);
        bitrate = Math.Clamp(bitrate, Capabilities.MinTargetMbps, Capabilities.MaxTargetMbps);
        Say($"  bitrate {bitrate:0.#} Mbps ({codec}){(note != null ? ": " + note : "")}; {Capabilities.DescribeEstimate(bitrate, audioTracks, secs)}; {Capabilities.DescribeRing(bitrate, secs).text}");

        // The same configuration AppController.BuildConfig makes, except for a short ring to keep the test light.
        (uint w, uint h) = AppController.OutputSize(mon, settings.Resolution);
        var cfg = new EngineConfig
        {
            Monitor = mon,
            OutWidth = w,
            OutHeight = h,
            Fps = (uint)Math.Clamp(settings.Fps, 15, 120),
            BitrateMbps = bitrate,
            Codec = codec,
            BufferSeconds = Math.Max(10, Math.Min(secs, 60)),
            CaptureCursor = settings.CaptureCursor,
            TrackDesktop = settings.TrackDesktop, TrackGame = settings.TrackGame, TrackMic = settings.TrackMic, TrackVoice = settings.TrackVoice,
            AllDeviceId = settings.AllDeviceId,
            MicDeviceId = settings.MicDeviceId,
            GameProcess = GameProcessTracker.NormalizeName(settings.GameProcess),
            VoiceApp = GameProcessTracker.NormalizeName(settings.VoiceApp),
            HdrKnee = settings.HdrKnee, HdrPeak = settings.HdrPeak,
            ClipsFolder = outDir ?? settings.ClipsFolder,
            GopSeconds = Math.Clamp(settings.KeyframeSeconds, 0.25f, 5f),
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
            Say($"Saved {clip.Path} ({clip.DurationSeconds:0.00}s, {clip.SizeBytes / 1_048_576.0:0.0} MB, audio [{string.Join(", ", clip.AudioTracks)}])");
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
