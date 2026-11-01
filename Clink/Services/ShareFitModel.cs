using System.Globalization;

namespace Clink.Services;

/// <summary>What a smaller copy has to fit: a byte limit, a length limit (0 = none; Copium has one, Discord does not) and the name the UI uses.</summary>
/// <param name="LimitBytes">The size the copy must not exceed.</param>
/// <param name="MaxSeconds">The longest clip the service takes, 0 = no length limit.</param>
/// <param name="Label">The limit as the UI says it ("Discord free (20 MB)"): <see cref="Capabilities.SizeTargets"/>' name for it.</param>
internal sealed record ShareFitTarget(long LimitBytes, int MaxSeconds, string Label)
{
    /// <summary>The service the copy is for, as in "a copy for Discord": "Discord" or "Copium".</summary>
    public string Service { get; init; } = "Discord";

    /// <summary>The short name in a refusal ("Too big for Copium: ..."): <see cref="Label"/> unless the service has a longer one.</summary>
    public string ShortLabel { get; init; } = Label;
}

/// <summary>What the share window (or a row, or a toast) shows for a clip and a target. The members after <see cref="Kind"/> are set for the kinds that use them.</summary>
internal enum ShareFitStateKind
{
    /// <summary>The clip is within the limit: share it as it is.</summary>
    Fits,
    /// <summary>Over the limit, nothing started: "Too big for ..." and the "Make a ... copy" button.</summary>
    Over,
    /// <summary>"Make a ... copy" was pressed and nothing is reported yet (the clip is being looked at): "Preparing...".</summary>
    Preparing,
    /// <summary>A copy is being made (<see cref="ShareFitState.Progress"/>, <see cref="ShareFitState.Plan"/> once planned).</summary>
    Working,
    /// <summary>Another copy is being made; this one starts after it.</summary>
    Waiting,
    /// <summary>The copy is ready (<see cref="ShareFitState.Copy"/>): share it, or "Use the original".</summary>
    Done,
    /// <summary>Not even the lowest quality fits; <see cref="ShareFitState.MaxSeconds"/> is the length to trim to.</summary>
    CantFit,
    /// <summary>The copy could not be made (<see cref="ShareFitState.Reason"/>).</summary>
    Failed,
    /// <summary>Smaller copies are not available here (<see cref="ShareFitState.Reason"/>: as administrator, a redirected cache folder); the original is shared.</summary>
    Unavailable,
    /// <summary>Refused on purpose (a game is in front and copies during games are off): <see cref="ShareFitState.Reason"/> is the whole text.</summary>
    Refused,
}

internal sealed record ShareFitState(ShareFitStateKind Kind)
{
    public double Progress { get; init; }
    public ShareFitPlan? Plan { get; init; }
    public ShareCopyResult? Copy { get; init; }
    public int MaxSeconds { get; init; }
    public string? Reason { get; init; }
    /// <summary>For Failed: why (from <see cref="ShareCopyResult.ErrorKind"/>). None when the state was built from a reason alone.</summary>
    public ShareCopyError ErrorKind { get; init; }

    public static readonly ShareFitState Fits = new(ShareFitStateKind.Fits);
    public static readonly ShareFitState Over = new(ShareFitStateKind.Over);
    public static readonly ShareFitState Waiting = new(ShareFitStateKind.Waiting);
    public static readonly ShareFitState Preparing = new(ShareFitStateKind.Preparing);
    public static ShareFitState Working(double progress, ShareFitPlan? plan) => new(ShareFitStateKind.Working) { Progress = Math.Clamp(progress, 0, 1), Plan = plan };
    public static ShareFitState Done(ShareCopyResult copy) => new(ShareFitStateKind.Done) { Copy = copy };
    public static ShareFitState CantFit(int maxSeconds) => new(ShareFitStateKind.CantFit) { MaxSeconds = maxSeconds };
    public static ShareFitState Failed(string reason) => new(ShareFitStateKind.Failed) { Reason = reason, ErrorKind = reason == ShareFitModel.StayedTooBigReason ? ShareCopyError.StayedTooBig : ShareCopyError.None };
    public static ShareFitState Failed(string reason, ShareCopyError kind) => new(ShareFitStateKind.Failed) { Reason = reason, ErrorKind = kind };
    public static ShareFitState Unavailable(string reason) => new(ShareFitStateKind.Unavailable) { Reason = reason };
    public static ShareFitState Refused(string text) => new(ShareFitStateKind.Refused) { Reason = text };
}

/// <summary>
/// The share-fit feature without any UI or I/O: the target a plan or service maps to, the state a clip is in, and every text the
/// share window, the multi-file window and the Copium row show. The bytes and labels of a Discord plan come only from
/// <see cref="Capabilities.SizeTargets"/> (the plan ids map to the target ids discord10 / discord50 / discord500).
/// </summary>
internal static class ShareFitModel
{
    /// <summary>The cache variant text of a share copy: another planner version gives another key, so an old copy is not reused.</summary>
    public const string CacheVariant = "fit-v1";
    /// <summary>The output codec of a share copy.</summary>
    public const string Codec = "h264";
    public const string UseOriginalText = "Use the original";
    public const string WaitingText = "Waiting...";
    public const string PreparingText = "Preparing...";
    public const string CancelText = "Cancel";

    // ---- the "My Discord plan" choices (the SHARING card) ----------------------------------------------------------------------------

    /// <summary>The "My Discord plan" choices in the order the card lists them: the plan id (<see cref="Settings.DiscordPlans"/>) and its label ("Discord free (20 MB)", ...) from <see cref="Capabilities.SizeTargets"/>.</summary>
    public static IReadOnlyList<(string Id, string Label)> DiscordPlanChoices()
        => Settings.DiscordPlans.Select(id => (id, Capabilities.SizeTargetFor(TargetIdFor(id)).Name)).ToArray();

    /// <summary>A copy needs this much free disk space (the copy, its muxer temporary and some room) before it starts: 2.2 x the limit + 64 MB.</summary>
    public static long RequiredFreeBytes(long limitBytes) => (Math.Max(0, limitBytes) * 11 + 4) / 5 + 64L * 1024 * 1024;   // 2.2 x, rounded up, in integers

    // ---- the target ---------------------------------------------------------------------------------------------------------------

    /// <summary>The target id a Discord plan id maps to ("free" = discord10, "basic" = discord50, "nitro" = discord500; unknown or null = free).</summary>
    public static string TargetIdFor(string? plan) => (plan ?? "").Trim().ToLowerInvariant() switch
    {
        "basic" => "discord50",
        "nitro" => "discord500",
        _ => "discord10",
    };

    /// <summary>The target for a Discord plan id ("free" | "basic" | "nitro"): 20 MB / 50 MB / 1 GB, and the label, from <see cref="Capabilities.SizeTargets"/>.</summary>
    public static ShareFitTarget ForPlan(string? plan)
    {
        var t = Capabilities.SizeTargetFor(TargetIdFor(plan));
        return new ShareFitTarget(t.Bytes, t.MaxSeconds, t.Name);
    }

    /// <summary>The target of the user's Discord plan setting.</summary>
    public static ShareFitTarget ForSettings(Settings settings) => ForPlan(Settings.NormalizeDiscordPlan(settings.DiscordPlan, settings.SizeTarget));

    /// <summary>Copium's size and length limits (free, or Plus) as a target, for the Copium upload (2e).</summary>
    public static ShareFitTarget ForCopium(bool plus)
    {
        var t = Capabilities.SizeTargetFor(plus ? "copiumplus" : "copium200");
        return new ShareFitTarget(t.Bytes, t.MaxSeconds, t.Name) { Service = "Copium", ShortLabel = plus ? "Copium Plus" : "Copium" };
    }

    // ---- the state ----------------------------------------------------------------------------------------------------------------

    /// <summary>Fits when <paramref name="originalBytes"/> is within the limit (or there is none), else Over.</summary>
    public static ShareFitState StateFor(long originalBytes, ShareFitTarget target)
        => target.LimitBytes <= 0 || originalBytes <= target.LimitBytes ? ShareFitState.Fits : ShareFitState.Over;

    public static ShareFitState StateFor(ShareCopyProgress progress)
        => progress.Stage == ShareCopyStage.Waiting ? ShareFitState.Waiting : ShareFitState.Working(progress.Fraction, progress.Plan);

    /// <summary>The state a finished job leaves: Done for a copy, Fits / Unavailable for the original, and so on. A cancel is Over again (the button comes back).</summary>
    public static ShareFitState StateFor(ShareCopyResult result) => result.Outcome switch
    {
        ShareCopyOutcome.UseCopy => ShareFitState.Done(result),
        ShareCopyOutcome.UseOriginal => result.Error == null ? ShareFitState.Fits : ShareFitState.Unavailable(result.Error),
        ShareCopyOutcome.CantFit => ShareFitState.CantFit(result.MaxSeconds),
        ShareCopyOutcome.Refused => ShareFitState.Refused(result.Error ?? ""),
        ShareCopyOutcome.Cancelled => ShareFitState.Over,
        _ => ShareFitState.Failed(result.Error ?? "something went wrong", result.ErrorKind) with { MaxSeconds = result.MaxSeconds },
    };

    // ---- the texts ----------------------------------------------------------------------------------------------------------------

    /// <summary>"Too big for Discord free (20 MB): this clip is 46 MB"</summary>
    public static string OverText(ShareFitTarget target, long originalBytes)
        => $"Too big for {target.Label}: this clip is {Capabilities.FormatBytes(originalBytes)}";

    /// <summary>"Make a 20 MB copy" (the button).</summary>
    public static string MakeCopyText(ShareFitTarget target) => $"Make a {Capabilities.FormatBytes(target.LimitBytes)} copy";

    /// <summary>
    /// "Making a 20 MB copy for Discord: 720p60 at 3.1 Mbps... 42%" (before the plan is known: "Making a 20 MB copy for Discord... 3%").
    /// For Copium: "Making a smaller copy for Copium... 40%".
    /// </summary>
    public static string WorkingText(ShareFitTarget target, ShareFitPlan? plan, double progress)
    {
        int percent = (int)Math.Round(Math.Clamp(progress, 0, 1) * 100, MidpointRounding.AwayFromZero);
        if (target.Service == "Copium") return $"Making a smaller copy for Copium... {percent}%";
        string what = $"Making a {Capabilities.FormatBytes(target.LimitBytes)} copy for {target.Service}";
        if (plan != null) what += $": {plan.Label} at {plan.VideoMbps.ToString("0.#", CultureInfo.InvariantCulture)} Mbps";
        return $"{what}... {percent}%";
    }

    /// <summary>"19.1 MB · 0:45 · 720p60": what the finished copy is (the size to one decimal below 1 GB, the clip length, the rung when known).</summary>
    public static string DoneDetails(long bytes, double seconds, string? label)
    {
        string size = bytes >= 1_000_000 && bytes < 1_000_000_000 ? (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : Capabilities.FormatBytes(bytes);
        string text = size + " · " + Capabilities.FormatClipLength(seconds);
        return string.IsNullOrEmpty(label) ? text : text + " · " + label;
    }

    /// <summary>"Trim it to about 0:55" (no full stop: the caller adds one or puts it on a line of its own).</summary>
    public static string TrimHint(int maxSeconds) => $"Trim it to about {Capabilities.FormatClipLength(maxSeconds)}";

    /// <summary>"A 3:20 clip can't fit in 20 MB. Trim it to about 0:55." (without a length to trim to: "A 3:20 clip can't fit in 20 MB."; a clip of unknown length: "This clip can't fit in 20 MB.")</summary>
    public static string CantFitText(ShareFitTarget target, double clipSeconds, int maxSeconds)
    {
        string clip = clipSeconds > 0 ? $"A {Capabilities.FormatClipLength(clipSeconds)} clip" : "This clip";
        string text = $"{clip} can't fit in {Capabilities.FormatBytes(target.LimitBytes)}.";
        return maxSeconds > 0 ? $"{text} {TrimHint(maxSeconds)}." : text;
    }

    /// <summary>"Couldn't make a smaller copy: the disk is full. The original is 46 MB, over Discord free (20 MB)."</summary>
    public static string FailedText(ShareFitTarget target, string reason, long originalBytes)
        => $"Couldn't make a smaller copy: {reason}. The original is {Capabilities.FormatBytes(originalBytes)}, over {target.Label}.";

    /// <summary>The copy stayed over the limit through every attempt: <see cref="FailedText"/> with that reason, and the length to trim to when there is one.</summary>
    public static string StayedTooBigText(ShareFitTarget target, long originalBytes, int maxSeconds)
    {
        string text = FailedText(target, "the copy stayed too big", originalBytes);
        return maxSeconds > 0 ? $"{text} {TrimHint(maxSeconds)}." : text;
    }

    /// <summary>"Smaller copies are not available while Clink runs as administrator. The original is 46 MB, over Discord free (20 MB)."</summary>
    public static string UnavailableText(ShareFitTarget target, string reason, long originalBytes)
        => $"Smaller copies are {reason}. The original is {Capabilities.FormatBytes(originalBytes)}, over {target.Label}.";

    /// <summary>"Too big for Copium: open Clink to make a smaller copy" (a game is in front and copies during games are off).</summary>
    public static string GameRefusalText(ShareFitTarget target) => $"Too big for {target.ShortLabel}: open Clink to make a smaller copy";

    /// <summary>"2 files are over Discord free (20 MB): share them one at a time." and, for one, "1 file is over Discord free (20 MB): share it on its own." (null for none).</summary>
    public static string? MultiFileNote(ShareFitTarget target, int overCount) => overCount switch
    {
        <= 0 => null,
        1 => $"1 file is over {target.Label}: share it on its own.",
        _ => $"{overCount} files are over {target.Label}: share them one at a time.",
    };

    /// <summary>The not-enough-disk reason: "there is not enough free disk space (about 2.3 GB are needed)".</summary>
    public static string LowDiskReason(long limitBytes) => $"there is not enough free disk space (about {Capabilities.FormatBytes(RequiredFreeBytes(limitBytes))} are needed)";

    /// <summary>The line the window shows for a state (null for Fits: nothing to say). <paramref name="clipSeconds"/> is the clip's length (for the can't-fit text).</summary>
    public static string? Text(ShareFitState state, ShareFitTarget target, long originalBytes, double clipSeconds) => state.Kind switch
    {
        ShareFitStateKind.Fits => null,
        ShareFitStateKind.Over => OverText(target, originalBytes),
        ShareFitStateKind.Preparing => PreparingText,
        ShareFitStateKind.Working => WorkingText(target, state.Plan, state.Progress),
        ShareFitStateKind.Waiting => WaitingText,
        ShareFitStateKind.Done => state.Copy is { } c ? DoneDetails(c.Bytes, c.Seconds, c.Label) : "",
        ShareFitStateKind.CantFit => CantFitText(target, clipSeconds, state.MaxSeconds),
        ShareFitStateKind.Failed => state.ErrorKind == ShareCopyError.StayedTooBig || state.Reason == StayedTooBigReason ? StayedTooBigText(target, originalBytes, state.MaxSeconds)
            : FailedText(target, state.Reason ?? "something went wrong", originalBytes),
        ShareFitStateKind.Unavailable => UnavailableText(target, state.Reason ?? "not available", originalBytes),
        ShareFitStateKind.Refused => state.Reason ?? GameRefusalText(target),
        _ => null,
    };

    /// <summary>
    /// Whether the window offers "Make a ... copy" in a state: Over; Refused (a game came forward while it waited; asking again may
    /// work); Failed only when another try may work, by <see cref="ShareFitState.ErrorKind"/>: LowDisk, DiskFull, GpuLost, Failed (any
    /// other error: an I/O error, the copy could not be saved) and None (a state built from a reason alone). Not for StayedTooBig (trim the
    /// clip), Unsupported, DecoderMissing, NoHardwareEncoder, Unreadable, FileMissing, CacheUnavailable or GameInFront: those fail the same
    /// way every time. Never for CantFit or Unavailable (nothing to make), nor while a copy is under way or in use. (A cancel is Over.)
    /// </summary>
    public static bool CanMakeCopy(ShareFitState state) => state.Kind switch
    {
        ShareFitStateKind.Over or ShareFitStateKind.Refused => true,
        ShareFitStateKind.Failed => state.ErrorKind is ShareCopyError.None or ShareCopyError.LowDisk or ShareCopyError.DiskFull
            or ShareCopyError.GpuLost or ShareCopyError.Failed,
        _ => false,
    };

    /// <summary>The <see cref="ShareCopyResult.Error"/> of a copy that stayed over the limit through every attempt.</summary>
    public const string StayedTooBigReason = "the copy stayed too big";
}
