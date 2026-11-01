using System.Globalization;

namespace Clink.Services;

/// <summary>What <see cref="ShareFit"/> decided.</summary>
internal enum ShareFitKind
{
    /// <summary>From <see cref="ShareFit.Plan"/>: the clip is already within the limit (or there is no limit); share the original.</summary>
    NoCopyNeeded,
    /// <summary>From <see cref="ShareFit.NextAttempt"/>: the copy that was just made is within the limit; share that copy.</summary>
    CopyFits,
    /// <summary>
    /// Keep the source video as it is (no re-encode) and keep only the Mix audio track; <see cref="ShareFitResult.EstimatedBytes"/>
    /// is what that should weigh. Only when the source already is in the output codec and the result is estimated to fit.
    /// If a RemuxOnly copy comes out over the limit, call Plan(..., allowRemux: false) once, then retry with NextAttempt as usual.
    /// A remux does not count toward MaxAttempts. A remux keeps the source resolution and frame rate, also above 1080p
    /// (intended: no re-encode means no quality loss).
    /// </summary>
    RemuxOnly,
    /// <summary>Make a smaller copy as described by <see cref="ShareFitResult.Plan"/>.</summary>
    Plan,
    /// <summary>Not even the lowest rung fits this clip's length; <see cref="ShareFitResult.MaxSeconds"/> is the longest clip to trim to.</summary>
    CantFit,
    /// <summary>The copy was made as planned but stayed over the limit through every attempt; <see cref="ShareFitResult.MaxSeconds"/> as for CantFit.</summary>
    Failed,
}

/// <summary>
/// The smaller copy to make: output size and frame rate, video bitrate, codec, the size it should come out at. The members after
/// <c>Label</c> are what <see cref="ShareFit.NextAttempt"/> needs to plan a retry (so the caller does not repeat them); callers can ignore them.
/// </summary>
/// <param name="Width">Output width in pixels (even, at least 2).</param>
/// <param name="Height">Output height in pixels (even, at least 2).</param>
/// <param name="Fps">
/// Output frames per second, a whole number (the encoder config takes an integer): a 59.94 or 29.97 fps source is 60 or 30 here, so
/// the frame picker must choose source frames by timestamp (the nearest to each output tick), not by counting.
/// </param>
/// <param name="VideoMbps">Video bitrate, in 0.1 Mbps steps.</param>
/// <param name="VideoBitrateBps">The same bitrate in bits per second, rounded once (use this for the encoder).</param>
/// <param name="Codec">"h264" | "hevc" | "av1".</param>
/// <param name="EstimatedBytes">(video + audio) × seconds / 8: what the copy should weigh.</param>
/// <param name="Label">The rung, as the UI says it: "720p60".</param>
/// <param name="SourceWidth">Width of the clip being shrunk.</param>
/// <param name="SourceHeight">Height of the clip being shrunk.</param>
/// <param name="SourceFps">Frame rate of the clip being shrunk.</param>
/// <param name="RungIndex">Position in the ladder built for this source (0 = best).</param>
/// <param name="FloorMbps">Lowest bitrate this rung is meant to run at (already scaled for codec and shape).</param>
/// <param name="Attempt">1 for the first plan, 2 and 3 for the retries.</param>
/// <param name="DurationSeconds">Length of the clip being shrunk.</param>
/// <param name="AudioKbps">The one kept audio track.</param>
/// <param name="SourceKeyframeSeconds">The keyframe interval of the source (a trim can start that much early).</param>
/// <param name="LimitBytes">The share limit this plan is for.</param>
internal sealed record ShareFitPlan(
    int Width, int Height, int Fps, double VideoMbps, long VideoBitrateBps, string Codec, long EstimatedBytes, string Label,
    int SourceWidth, int SourceHeight, double SourceFps, int RungIndex, double FloorMbps, int Attempt,
    double DurationSeconds, int AudioKbps, double SourceKeyframeSeconds, long LimitBytes);

/// <summary>
/// A planner answer: <see cref="Plan"/> is set for <see cref="ShareFitKind.Plan"/>, <see cref="MaxSeconds"/> for CantFit and Failed,
/// <see cref="EstimatedBytes"/> for RemuxOnly.
/// </summary>
internal sealed record ShareFitResult(ShareFitKind Kind, ShareFitPlan? Plan, int MaxSeconds, long EstimatedBytes = 0)
{
    public static readonly ShareFitResult NoCopyNeeded = new(ShareFitKind.NoCopyNeeded, null, 0);
    public static readonly ShareFitResult CopyFits = new(ShareFitKind.CopyFits, null, 0);
}

/// <summary>
/// Decides which smaller copy of a clip to make so that it fits a share limit (a Discord plan, Copium's size limit). Pure and
/// deterministic: numbers in, numbers out, no I/O and no culture-dependent logic. Quality is reduced only as far as needed.
/// <para>
/// Order of answers: a source within the limit is shared as it is; else, if the source video is already in the output codec and
/// keeping just the Mix track would fit, <see cref="ShareFitKind.RemuxOnly"/>; else the best rung that fits
/// (<see cref="ShareFitKind.Plan"/>); else <see cref="ShareFitKind.CantFit"/> with a length to trim to.
/// </para>
/// <para>
/// Budget: the limit × <see cref="Capabilities.TargetHeadroom"/> over the duration, minus the one kept audio track, floored to
/// 0.1 Mbps (like <see cref="Capabilities.ResolveBitrate"/>; <see cref="Capabilities.BitrateMbpsFor"/> does the arithmetic and
/// is not given the headroom twice) and capped at <see cref="Capabilities.MaxTargetMbps"/>. A re-encode is also never given more
/// than the bitrate the source video is estimated to have (source bytes less its audio tracks), but not under
/// <see cref="Capabilities.MinTargetMbps"/>.
/// </para>
/// <para>
/// Ladder, best first, resolution before frame rate: 1080p60 ≥ 5 Mbps, 720p60 ≥ 2.5, 480p60 ≥ 1.2, 480p30 ≥ 0.7 (H.264, 16:9).
/// A source of 30 fps or less never gets a 60 fps rung: 1080p30 ≥ 3.0, 720p30 ≥ 1.5, 480p30 ≥ 0.7, at its own rate (24 stays 24).
/// The first rung whose floor is at or below the budget wins. HEVC and AV1 floors are 0.7× the H.264 ones. A rung is named by
/// the SHORT side of the frame (so portrait clips work); the picture keeps its aspect ratio with even dimensions; the floor
/// scales with the shape: floor × (long/short) ÷ (16/9), so an ultrawide needs more and a 4:3 frame less than a 16:9 one.
/// Never upscaled, and 1080p is the top. A source under 480p has one rung size, its own, at the 480p floors scaled by pixel
/// area. A source between standard sizes (1600x900) tops the ladder with its own size, floors = the standard rung below it
/// scaled by area, at most the rung above's floor; then come the standard rungs under it.
/// </para>
/// <para>
/// Frame rates: a source at or under the rung's class (30 or 60) keeps its rate. Above it, the rate is the source's divided by a
/// whole number (an even decimation: 50 → 25, 120 → 30, 144 → 24 / 48) as long as the result is a whole number of at least 24
/// (30 class) or 48 (60 class), else the class rate (uneven; the frame picker works by timestamps).
/// </para>
/// <para>
/// Trim hints: the suggested length is the longest clip, in whole seconds, that still fits the lowest rung, less the source's
/// keyframe interval (a trim starts at the keyframe at or before the requested start, up to that much early).
/// </para>
/// </summary>
internal static class ShareFit
{
    /// <summary>The most encodes one share may take (the first plan and two retries).</summary>
    public const int MaxAttempts = 3;
    /// <summary>HEVC and AV1 reach the same quality at this share of the H.264 bitrate, so their floors are this share too.</summary>
    public const double ModernCodecFloorFactor = 0.7;
    /// <summary>A retry aims this much under the size the last attempt should have had, so rate-control overshoot does not repeat.</summary>
    public const double RetryMargin = 0.97;
    /// <summary>The settings default for the source's keyframe interval, used when the caller does not know it.</summary>
    public const double DefaultKeyframeSeconds = 1.0;

    // Short sides of the standard rungs, best first, with their H.264 floors (Mbps) at 60 fps and at 30 fps, for a 16:9 frame.
    private static readonly (int side, double floor60, double floor30)[] s_sides =
    {
        (1080, 5.0, 3.0),
        (720, 2.5, 1.5),
        (480, 1.2, 0.7),
    };

    private const double Epsilon = 1e-9;

    private sealed record Rung(int Width, int Height, int Fps, double FloorMbps, string Label);

    private sealed record Ctx(int Width, int Height, double Fps, string Codec, double Seconds, int AudioKbps, double KeyframeSeconds, long Limit);

    /// <summary>
    /// What to do with a clip of this shape and length for a limit. <paramref name="limitBytes"/> of 0 or less means no limit, and
    /// a source at or under the limit needs no copy (neither needs a duration, size or frame rate). Otherwise the source needs bytes
    /// above 0, a short side of 2 or more, a frame rate above 0 and a duration above 0, else <see cref="ArgumentOutOfRangeException"/>.
    /// <paramref name="codec"/> is the output codec id ("h264" | "hevc" | "av1"; anything else is H.264).
    /// <paramref name="audioKbps"/> is the one kept audio track (the Mix, <see cref="Capabilities.AudioKbps"/>).
    /// <paramref name="sourceAudioTracks"/> and <paramref name="sourceCodec"/> (null or unknown: no remux) describe the source;
    /// <paramref name="sourceKeyframeSeconds"/> is clamped to 0.25 .. 5 like the engine's GOP; <paramref name="allowRemux"/> false
    /// skips <see cref="ShareFitKind.RemuxOnly"/> (when that was tried and came out too big).
    /// </summary>
    public static ShareFitResult Plan(
        int sourceWidth, int sourceHeight, double sourceFps, double durationSeconds, long sourceBytes, long limitBytes,
        string codec, int audioKbps = Capabilities.AudioKbps, int sourceAudioTracks = 1, string? sourceCodec = null,
        double sourceKeyframeSeconds = DefaultKeyframeSeconds, bool allowRemux = true)
    {
        if (limitBytes <= 0) return ShareFitResult.NoCopyNeeded;
        if (sourceBytes <= 0) throw new ArgumentOutOfRangeException(nameof(sourceBytes), "source size must be above 0");
        if (sourceBytes <= limitBytes) return ShareFitResult.NoCopyNeeded;
        if (sourceWidth < 2 || sourceHeight < 2) throw new ArgumentOutOfRangeException(nameof(sourceWidth), "source size must be at least 2x2");
        if (!(sourceFps > 0) || double.IsInfinity(sourceFps)) throw new ArgumentOutOfRangeException(nameof(sourceFps), "frame rate must be above 0");
        if (!(durationSeconds > 0) || double.IsInfinity(durationSeconds)) throw new ArgumentOutOfRangeException(nameof(durationSeconds), "duration must be above 0");
        codec = NormalizeCodec(codec);
        audioKbps = Math.Max(0, audioKbps);
        double keyframe = ClampKeyframe(sourceKeyframeSeconds);

        // What the source video weighs, from the file size less its audio tracks.
        double audioBytesPerTrack = audioKbps * 1000.0 * durationSeconds / 8;
        double sourceVideoBytes = Math.Max(0, sourceBytes - Math.Max(0, sourceAudioTracks) * audioBytesPerTrack);
        if (allowRemux && KnownCodec(sourceCodec) == codec)
        {
            double remuxBytes = sourceVideoBytes + audioBytesPerTrack;
            if (remuxBytes <= limitBytes * Capabilities.TargetHeadroom)
                return new ShareFitResult(ShareFitKind.RemuxOnly, null, 0, (long)Math.Round(remuxBytes));
        }

        var ladder = BuildLadder(sourceWidth, sourceHeight, sourceFps, codec);
        double budget = BudgetMbps(limitBytes, durationSeconds, audioKbps);
        // A more efficient source codec re-encoded to H.264 needs more bitrate for the same quality, so the cap is only ever raised.
        var srcKnown = KnownCodec(sourceCodec);
        double capScale = (srcKnown == "hevc" || srcKnown == "av1") && codec == "h264" ? 1 / ModernCodecFloorFactor : 1;
        double sourceMbps = Math.Max(Capabilities.MinTargetMbps, FloorToTenth(sourceVideoBytes * capScale * 8 / durationSeconds / 1_000_000));
        var ctx = new Ctx(sourceWidth, sourceHeight, sourceFps, codec, durationSeconds, audioKbps, keyframe, limitBytes);
        for (int i = 0; i < ladder.Count; i++)
            if (budget >= ladder[i].FloorMbps - Epsilon)
                return Planned(ladder, i, Math.Min(budget, sourceMbps), ctx, 1);
        return new ShareFitResult(ShareFitKind.CantFit, null, MaxSecondsAtLowest(ladder, limitBytes, audioKbps, keyframe, 1));
    }

    /// <summary>
    /// The plan for the next try after the copy came out at <paramref name="actualBytes"/> (above 0). The try number is
    /// <c>previous.Attempt + 1</c>; past <see cref="MaxAttempts"/> the answer is <see cref="ShareFitKind.Failed"/>. A copy within the
    /// limit is <see cref="ShareFitKind.CopyFits"/>. The new bitrate is the old × (target video bytes ÷ actual video bytes) × 0.97,
    /// the video bytes being the total less the audio's, the target being the limit with its headroom. Below the current rung's
    /// floor, the highest lower rung whose floor it reaches is used (usually the next one), at that bitrate; when no rung is left
    /// the answer is <see cref="ShareFitKind.CantFit"/>. For CantFit and Failed, <see cref="ShareFitResult.MaxSeconds"/> allows for
    /// the overshoot seen (the encoder did not hit its bitrate): the lowest rung's bitrate is scaled by actual ÷ planned video bytes
    /// (at least 1), and the result is under the clip's own length.
    /// </summary>
    public static ShareFitResult NextAttempt(ShareFitPlan previous, long actualBytes)
    {
        if (actualBytes <= 0) throw new ArgumentOutOfRangeException(nameof(actualBytes), "actual size must be above 0");
        if (actualBytes <= previous.LimitBytes) return ShareFitResult.CopyFits;
        var ladder = BuildLadder(previous.SourceWidth, previous.SourceHeight, previous.SourceFps, previous.Codec);
        int attempt = previous.Attempt + 1;
        double seconds = previous.DurationSeconds;
        int audioKbps = previous.AudioKbps;

        double audioBytes = audioKbps * 1000.0 * seconds / 8;
        double actualVideo = Math.Max(1, actualBytes - audioBytes);
        double plannedVideo = previous.VideoBitrateBps * seconds / 8;
        double overshoot = plannedVideo > 0 ? Math.Max(1, actualVideo / plannedVideo) : 1;
        int maxSeconds = Math.Min(
            MaxSecondsAtLowest(ladder, previous.LimitBytes, audioKbps, previous.SourceKeyframeSeconds, overshoot),
            (int)Math.Max(0, Math.Min(int.MaxValue, Math.Ceiling(seconds - previous.SourceKeyframeSeconds) - 1)));
        if (attempt > MaxAttempts) return new ShareFitResult(ShareFitKind.Failed, null, maxSeconds);

        double targetVideo = previous.LimitBytes * Capabilities.TargetHeadroom - audioBytes;
        double mbps = FloorToTenth(previous.VideoMbps * (Math.Max(0, targetVideo) / actualVideo) * RetryMargin);
        mbps = Math.Min(mbps, previous.VideoMbps);

        // Same rung while the bitrate still reaches its floor, else the highest lower rung it reaches.
        int rung = Math.Clamp(previous.RungIndex, 0, ladder.Count - 1);
        while (rung < ladder.Count && mbps < ladder[rung].FloorMbps - Epsilon) rung++;
        if (rung >= ladder.Count) return new ShareFitResult(ShareFitKind.CantFit, null, maxSeconds);
        var ctx = new Ctx(previous.SourceWidth, previous.SourceHeight, previous.SourceFps, previous.Codec, seconds, audioKbps,
            previous.SourceKeyframeSeconds, previous.LimitBytes);
        return Planned(ladder, rung, mbps, ctx, attempt);
    }

    /// <summary>"Trim it to about …": the longest clip, in whole seconds (rounded down, less the source keyframe interval), that fits the limit at the lowest rung for this source.</summary>
    public static int MaxSecondsFor(
        int sourceWidth, int sourceHeight, double sourceFps, long limitBytes, string codec, int audioKbps = Capabilities.AudioKbps,
        double sourceKeyframeSeconds = DefaultKeyframeSeconds)
    {
        if (sourceWidth < 2 || sourceHeight < 2 || !(sourceFps > 0) || double.IsInfinity(sourceFps) || limitBytes <= 0) return 0;
        return MaxSecondsAtLowest(BuildLadder(sourceWidth, sourceHeight, sourceFps, NormalizeCodec(codec)), limitBytes,
            Math.Max(0, audioKbps), ClampKeyframe(sourceKeyframeSeconds), 1);
    }

    private static ShareFitResult Planned(List<Rung> ladder, int index, double mbps, Ctx c, int attempt)
    {
        var r = ladder[index];
        long bps = (long)Math.Round(Math.Min(Capabilities.MaxTargetMbps, mbps) * 1_000_000);
        long bytes = (long)Math.Round((bps + c.AudioKbps * 1000.0) * c.Seconds / 8);
        var plan = new ShareFitPlan(r.Width, r.Height, r.Fps, bps / 1_000_000.0, bps, c.Codec, bytes, r.Label,
            c.Width, c.Height, c.Fps, index, r.FloorMbps, attempt, c.Seconds, c.AudioKbps, c.KeyframeSeconds, c.Limit);
        return new ShareFitResult(ShareFitKind.Plan, plan, 0);
    }

    /// <summary>The video bitrate that is left after the audio, in 0.1 Mbps steps, 0 when nothing is left.</summary>
    private static double BudgetMbps(long limitBytes, double seconds, int audioKbps)
    {
        double mbps = Capabilities.BitrateMbpsFor((long)(limitBytes * Capabilities.TargetHeadroom), 0, seconds) - audioKbps / 1000.0;
        return Math.Min(Capabilities.MaxTargetMbps, Math.Max(0, FloorToTenth(mbps)));
    }

    private static double FloorToTenth(double mbps) => Math.Floor(mbps * 10 + Epsilon) / 10;

    private static double ClampKeyframe(double seconds) =>
        double.IsNaN(seconds) ? DefaultKeyframeSeconds : Math.Clamp(seconds, 0.25, 5.0);

    /// <summary>
    /// The longest clip that fits the lowest rung when the encoder runs <paramref name="overshoot"/> times its bitrate, less the
    /// keyframe interval the trim can start early by.
    /// </summary>
    private static int MaxSecondsAtLowest(List<Rung> ladder, long limitBytes, int audioKbps, double keyframeSeconds, double overshoot)
    {
        // The rung must be reachable at the 0.1 Mbps the budget is floored to, so its floor is rounded up to a tenth.
        double need = Math.Ceiling(ladder[^1].FloorMbps * 10 - Epsilon) / 10;
        double bitsPerSecond = need * 1_000_000 * overshoot + audioKbps * 1000.0;
        double seconds = Math.Floor((long)(limitBytes * Capabilities.TargetHeadroom) * 8.0 / bitsPerSecond - keyframeSeconds);
        return (int)Math.Min(int.MaxValue, Math.Max(0, seconds));
    }

    private static string NormalizeCodec(string? codec) => KnownCodec(codec) is "hevc" or "av1" ? KnownCodec(codec)! : "h264";

    /// <summary>"h264" | "hevc" | "av1" for the ids the planner knows (also "avc"/"h265"), else null.</summary>
    private static string? KnownCodec(string? codec) => (codec ?? "").Trim().ToLowerInvariant() switch
    {
        "h264" or "avc" => "h264",
        "hevc" or "h265" => "hevc",
        "av1" => "av1",
        _ => null,
    };

    /// <summary>
    /// The frame rate for a rung class (<paramref name="max"/> 30 or 60): the source's own at or under it, else the source's divided
    /// by the smallest whole number that gives a whole rate of at least <paramref name="min"/> (an even decimation), else <paramref name="max"/>.
    /// </summary>
    private static int OutputFps(int rate, int max, int min)
    {
        if (rate <= max) return rate;
        for (int d = (rate + max - 1) / max; rate / d >= min; d++)
            if (rate % d == 0) return rate / d;
        return max;
    }

    private static List<Rung> BuildLadder(int width, int height, double fps, string codec)
    {
        int longSide = Math.Max(width, height), shortSide = Math.Min(width, height);
        bool portrait = height > width;
        int rate = Math.Max(1, (int)Math.Round(Math.Min(fps, 100_000), MidpointRounding.AwayFromZero));
        double codecFactor = codec == "h264" ? 1.0 : ModernCodecFloorFactor;
        double shape = longSide * 9.0 / (shortSide * 16.0);   // 1 for 16:9, 1.34375 for 3440x1440, 0.75 for 4:3
        int own = Math.Max(2, shortSide & ~1);                 // the source's short side with an odd pixel dropped

        // (short side drawn, 60 fps floor, 30 fps floor) for each size, before shape and codec.
        var sizes = new List<(int side, double floor60, double floor30)>();
        if (own < 1080 && Array.FindIndex(s_sides, x => x.side == own) < 0)
        {
            int below = Array.FindIndex(s_sides, x => x.side < own);
            if (below < 0)
            {
                // under 480p: the 480p floors scaled by pixel area
                double area = (own / 480.0) * (own / 480.0);
                sizes.Add((own, s_sides[^1].floor60 * area, s_sides[^1].floor30 * area));
            }
            else
            {
                // between two standard sizes: the one below, scaled by area, but no more than the one above
                double area = (own / (double)s_sides[below].side) * (own / (double)s_sides[below].side);
                sizes.Add((own, Math.Min(s_sides[below].floor60 * area, s_sides[below - 1].floor60),
                    Math.Min(s_sides[below].floor30 * area, s_sides[below - 1].floor30)));
            }
        }
        foreach (var s in s_sides)
            if (s.side <= own) sizes.Add(s);

        var rungs = new List<(int side, double floor, bool fast)>();
        if (rate > 30)
        {
            foreach (var s in sizes) rungs.Add((s.side, s.floor60, true));
            rungs.Add((sizes[^1].side, sizes[^1].floor30, false));
        }
        else
        {
            foreach (var s in sizes) rungs.Add((s.side, s.floor30, false));
        }

        var ladder = new List<Rung>(rungs.Count);
        foreach (var (side, baseFloor, fast) in rungs)
        {
            double floor = baseFloor * shape * codecFactor;
            int outFps = fast ? OutputFps(rate, 60, 48) : OutputFps(rate, 30, 24);
            // At (or one pixel under) the source's own short side the picture is the source's, with odd pixels dropped.
            int outShort, outLong;
            if (side >= shortSide - 1) { outShort = Math.Max(2, shortSide & ~1); outLong = Math.Max(2, longSide & ~1); }
            else
            {
                outShort = side;
                outLong = Math.Max(2, 2 * (int)Math.Round(longSide * (double)side / shortSide / 2, MidpointRounding.AwayFromZero));
            }
            int w = portrait ? outShort : outLong, h = portrait ? outLong : outShort;
            ladder.Add(new Rung(w, h, outFps, floor, side.ToString(CultureInfo.InvariantCulture) + "p" + outFps.ToString(CultureInfo.InvariantCulture)));
        }
        return ladder;
    }
}
