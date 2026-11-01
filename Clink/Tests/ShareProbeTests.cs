using System.IO;
using Clink.Engine;

namespace Clink.Tests;

/// <summary>The header-only probe (<see cref="ShareProbe"/>) on synthetic clips made like the transcoder tests' (software H.264 and AAC through the recording's own muxer). No GPU.</summary>
internal static class ShareProbeTests
{
    [UnitTest]
    public static void The_probe_reads_length_codec_size_rate_and_audio_tracks_from_the_header()
    {
        using var dir = new TempDir();
        ShareTranscoderTests.OnMta(() =>
        {
            string src = ShareTranscoderTests.MakeClip(dir, "src.mp4", 640, 360, 60, 2.0, "Mix", "Game", "Mic");
            var before = new FileInfo(src).Length;
            var p = ShareProbe.Read(src, 2.0);
            Check.Near(2.0, p.DurationSeconds, 0.15, "MF_PD_DURATION");
            Check.Equal("h264", p.Codec);
            Check.Equal((640, 360), (p.Width, p.Height));
            Check.Near(60, p.Fps, 0.5, "the frame rate");
            Check.Equal(3, p.AudioTracks);
            Check.Near(2.0, p.KeyframeSeconds, 1e-9, "the setting passed in");
            Check.Equal("360p60", p.Label);
            Check.Equal(before, new FileInfo(src).Length, "nothing written");

            string silent = ShareTranscoderTests.MakeClip(dir, "silent.mp4", 320, 180, 30, 1.0);
            var s = ShareProbe.Read(silent, 1.0);
            Check.Equal(0, s.AudioTracks, "no audio");
            Check.Near(30, s.Fps, 0.5);
            Check.Equal((320, 180), (s.Width, s.Height));
        });
    }

    [UnitTest]
    public static void The_probe_gives_a_typed_reason_for_what_is_not_a_video()
    {
        using var dir = new TempDir();
        string bad = dir.File("not a clip.mp4");
        File.WriteAllBytes(bad, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        ShareTranscoderTests.OnMta(() =>
        {
            var e = Check.Throws<ShareTranscodeException>(() => ShareProbe.Read(bad, 1.0), "garbage");
            Check.Equal(ShareTranscodeFailure.Unsupported, e.Kind, e.Message);
            var gone = Check.Throws<ShareTranscodeException>(() => ShareProbe.Read(dir.File("gone.mp4"), 1.0), "missing");
            Check.Equal(ShareTranscodeFailure.Unsupported, gone.Kind, gone.Message);
        });
    }

    [UnitTest]
    public static void The_keyframe_interval_is_clamped_like_the_engines_GOP()
    {
        Check.Equal(0.25, ShareProbe.ClampKeyframe(0.0));
        Check.Equal(0.25, ShareProbe.ClampKeyframe(-3));
        Check.Equal(1.0, ShareProbe.ClampKeyframe(1.0));
        Check.Equal(5.0, ShareProbe.ClampKeyframe(60));
        Check.Equal(1.0, ShareProbe.ClampKeyframe(double.NaN), "unreadable = the 1 s default");
        Check.Equal(5.0, ShareProbe.ClampKeyframe(double.PositiveInfinity));
    }

    [UnitTest]
    public static void A_frame_rate_comes_from_the_header_or_the_first_sample_gap()
    {
        Check.Equal(60.0, ShareProbe.RateOf(60, 1));
        Check.Near(59.94, ShareProbe.RateOf(60000, 1001), 0.001);
        Check.Equal(0.0, ShareProbe.RateOf(0, 1), "no numerator");
        Check.Equal(0.0, ShareProbe.RateOf(30, 0), "no denominator");
        Check.Equal(0.0, ShareProbe.RateOf(90_000, 1), "an absurd rate is no rate");
        Check.Near(60, ShareProbe.FromDelta(166_667), 0.01, "16.67 ms = 60 fps");
        Check.Near(30, ShareProbe.FromDelta(333_333), 0.01);
        Check.Equal(0.0, ShareProbe.FromDelta(0), "the same time twice");
        Check.Equal(0.0, ShareProbe.FromDelta(-5), "going backwards");
        Check.Equal(0.0, ShareProbe.FromDelta(20_000_000), "more than a second apart is no frame rate");
        Check.Equal("720p60", ShareProbe.LabelFor(1280, 720, 59.94));
        Check.Equal("1080p30", ShareProbe.LabelFor(1080, 1920, 30), "a portrait clip is named by its short side");
    }
}
