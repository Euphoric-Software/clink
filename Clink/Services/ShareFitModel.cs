using System.Globalization;
using Clink.Services.Localization;

namespace Clink.Services;

/// <summary>What a smaller copy has to fit: a byte limit, a length limit (0 = none; Copium has one, Discord does not) and the name the UI uses.</summary>
/// <param name="LimitBytes">The size the copy must not exceed.</param>
/// <param name="MaxSeconds">The longest clip the service takes, 0 = no length limit.</param>
/// <param name="Label">The limit as the UI says it ("Discord free (20 MB)"): <see cref="Capabilities.SizeTargets"/>' name for it.</param>
internal sealed record ShareFitTarget(long LimitBytes, int MaxSeconds, string Label)
{
    private readonly string? _shortLabel;

    /// <summary>The service the copy is for, as in "a copy for Discord": "Discord" or "Copium".</summary>
    public string Service { get; init; } = "Discord";

    /// <summary>
    /// The catalog key of <see cref="Label"/> (<see cref="SizeTargetInfo.NameKey"/>), so a <see cref="Note"/> keeps the plan name as a key and
    /// its English stays English whatever language <see cref="Label"/> was built in. Null for a label that is plain text (a test target).
    /// </summary>
    public string? NameKey { get; init; }

    /// <summary>The short name in a refusal ("Too big for Copium: ..."): <see cref="Label"/> unless the service has a longer one.</summary>
    public string ShortLabel { get => _shortLabel ?? Label; init => _shortLabel = value; }

    /// <summary><see cref="Label"/> as a note argument: the catalog entry <see cref="NameKey"/> (shown translated, logged in English), else the text as it is.</summary>
    public Note LabelNote => NameKey != null ? Note.Of(NameKey) : Note.Verbatim(Label);

    /// <summary><see cref="ShortLabel"/> as a note argument: a product name set by the service is never translated; the default is <see cref="LabelNote"/>.</summary>
    public Note ShortLabelNote => _shortLabel != null ? Note.Verbatim(_shortLabel) : LabelNote;
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
    /// <summary>English: the reason of a Failed state, the whole text of a Refused one (logs, tests). The user is shown <see cref="ReasonNote"/> / <see cref="ShareFitModel.TextNote"/>.</summary>
    public string? Reason { get; init; }
    /// <summary>The reason as a note (a Failed state made from a result that carried one); null: <see cref="Reason"/> is shown as it is.</summary>
    public Note? ReasonNote { get; init; }
    /// <summary>For Unavailable: why smaller copies are not available.</summary>
    public ShareUnavailableKind Why { get; init; }
    /// <summary>For Failed: why (from <see cref="ShareCopyResult.ErrorKind"/>). None when the state was built from a reason alone.</summary>
    public ShareCopyError ErrorKind { get; init; }

    public static readonly ShareFitState Fits = new(ShareFitStateKind.Fits);
    public static readonly ShareFitState Over = new(ShareFitStateKind.Over);
    public static readonly ShareFitState Waiting = new(ShareFitStateKind.Waiting);
    public static readonly ShareFitState Preparing = new(ShareFitStateKind.Preparing);
    public static ShareFitState Working(double progress, ShareFitPlan? plan) => new(ShareFitStateKind.Working) { Progress = Math.Clamp(progress, 0, 1), Plan = plan };
    public static ShareFitState Done(ShareCopyResult copy) => new(ShareFitStateKind.Done) { Copy = copy };
    public static ShareFitState CantFit(int maxSeconds) => new(ShareFitStateKind.CantFit) { MaxSeconds = maxSeconds };
    /// <summary>A failed copy: the English <paramref name="reason"/> (logs, tests) and what <paramref name="kind"/> of failure it was (the model acts on the kind, never on the words).</summary>
    public static ShareFitState Failed(string reason, ShareCopyError kind) => new(ShareFitStateKind.Failed) { Reason = reason, ErrorKind = kind };
    public static ShareFitState Unavailable(ShareUnavailableKind why) => new(ShareFitStateKind.Unavailable) { Why = why, Reason = ShareFitModel.UnavailableReason(why) };
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
    // (properties, not constants: the words follow the language of the UI)
    public static string UseOriginalText => L.T("Use the original");
    public static string WaitingText => L.T("Waiting...");
    public static string PreparingText => L.T("Preparing...");
    public static string CancelText => L.T("Cancel");

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
        return new ShareFitTarget(t.Bytes, t.MaxSeconds, t.Name) { NameKey = t.NameKey };
    }

    /// <summary>The target of the user's Discord plan setting.</summary>
    public static ShareFitTarget ForSettings(Settings settings) => ForPlan(Settings.NormalizeDiscordPlan(settings.DiscordPlan, settings.SizeTarget));

    /// <summary>Copium's size and length limits (free, or Plus) as a target, for the Copium upload (2e).</summary>
    public static ShareFitTarget ForCopium(bool plus)
    {
        var t = Capabilities.SizeTargetFor(plus ? "copiumplus" : "copium200");
        return new ShareFitTarget(t.Bytes, t.MaxSeconds, t.Name) { Service = "Copium", ShortLabel = plus ? "Copium Plus" : "Copium", NameKey = t.NameKey };
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
        ShareCopyOutcome.UseOriginal => result.Error == null ? ShareFitState.Fits : ShareFitState.Unavailable(result.Unavailable),
        ShareCopyOutcome.CantFit => ShareFitState.CantFit(result.MaxSeconds),
        ShareCopyOutcome.Refused => ShareFitState.Refused(result.Error ?? "") with { ErrorKind = result.ErrorKind },
        ShareCopyOutcome.Cancelled => ShareFitState.Over,
        _ => ShareFitState.Failed(result.Error ?? "something went wrong", result.ErrorKind) with { MaxSeconds = result.MaxSeconds, ReasonNote = result.ErrorNote ?? (result.Error == null ? Note.Of("reason|something went wrong") : null) },
    };

    // ---- the texts ----------------------------------------------------------------------------------------------------------------
    // Each text is a Note (catalog key + arguments): shown in the language of the UI, logged in English. The *Text methods are the shown form.

    /// <summary>"Too big for Discord free (20 MB): this clip is 46 MB"</summary>
    public static Note OverNote(ShareFitTarget target, long originalBytes)
        => Note.Of("Too big for {label}: this clip is {size}", ("label", target.LabelNote), ("size", Note.Size(originalBytes)));

    /// <summary>"Too big for Discord free (20 MB): this clip is 46 MB"</summary>
    public static string OverText(ShareFitTarget target, long originalBytes) => OverNote(target, originalBytes).Shown;

    /// <summary>"Make a 20 MB copy" (the button).</summary>
    public static string MakeCopyText(ShareFitTarget target) => L.T("Make a {size} copy", ("size", Capabilities.FormatBytes(target.LimitBytes)));

    /// <summary>
    /// "Making a 20 MB copy for Discord: 720p60 at 3.1 Mbps... 42%" (before the plan is known: "Making a 20 MB copy for Discord... 3%").
    /// For Copium: "Making a smaller copy for Copium... 40%".
    /// </summary>
    public static Note WorkingNote(ShareFitTarget target, ShareFitPlan? plan, double progress)
    {
        int percent = (int)Math.Round(Math.Clamp(progress, 0, 1) * 100, MidpointRounding.AwayFromZero);
        var pct = ("percent", (object?)(percent / 100.0).ToString("P0", L.FormatCulture));
        if (target.Service == "Copium") return Note.Of("Making a smaller copy for Copium... {percent}", pct);
        var size = ("size", (object?)Note.Size(target.LimitBytes));
        var service = ("service", (object?)target.Service);   // a product name
        if (plan == null) return Note.Of("Making a {size} copy for {service}... {percent}", size, service, pct);
        return Note.Of("Making a {size} copy for {service}: {label} at {mbps} Mbps... {percent}", size, service, ("label", plan.Label), ("mbps", plan.VideoMbps.ToString("0.#", L.FormatCulture)), pct);
    }

    public static string WorkingText(ShareFitTarget target, ShareFitPlan? plan, double progress) => WorkingNote(target, plan, progress).Shown;

    /// <summary>"19.1 MB · 0:45 · 720p60": what the finished copy is (the size to one decimal below 1 GB, the clip length, the rung when known).</summary>
    public static string DoneDetails(long bytes, double seconds, string? label)
    {
        string size = bytes >= 1_000_000 && bytes < 1_000_000_000 ? L.T("unit|{n} MB", ("n", (bytes / 1_000_000.0).ToString("0.0", L.FormatCulture))) : Capabilities.FormatBytes(bytes);
        string text = size + " · " + Capabilities.FormatClipLength(seconds);
        return string.IsNullOrEmpty(label) ? text : text + " · " + label;
    }

    /// <summary>"Trim it to about 0:55" (no full stop: the caller adds one or puts it on a line of its own).</summary>
    public static string TrimHint(int maxSeconds) => L.T("Trim it to about {length}", ("length", Capabilities.FormatClipLength(maxSeconds)));

    /// <summary>"A 3:20 clip can't fit in 20 MB. Trim it to about 0:55." (without a length to trim to: "A 3:20 clip can't fit in 20 MB."; a clip of unknown length: "This clip can't fit in 20 MB.")</summary>
    public static Note CantFitNote(ShareFitTarget target, double clipSeconds, int maxSeconds)
    {
        var size = ("size", (object?)Note.Size(target.LimitBytes));
        var length = ("length", (object?)Capabilities.FormatClipLength(clipSeconds));
        var trim = ("trim", (object?)Capabilities.FormatClipLength(maxSeconds));
        if (clipSeconds > 0)
            return maxSeconds > 0 ? Note.Of("A {length} clip can't fit in {size}. Trim it to about {trim}.", length, size, trim) : Note.Of("A {length} clip can't fit in {size}.", length, size);
        return maxSeconds > 0 ? Note.Of("This clip can't fit in {size}. Trim it to about {trim}.", size, trim) : Note.Of("This clip can't fit in {size}.", size);
    }

    public static string CantFitText(ShareFitTarget target, double clipSeconds, int maxSeconds) => CantFitNote(target, clipSeconds, maxSeconds).Shown;

    /// <summary>"the copy stayed too big": the reason of a copy that was over the limit through every attempt (the English text is <see cref="StayedTooBigReason"/>).</summary>
    public static Note StayedTooBigReasonNote => Note.Of("reason|the copy stayed too big");

    /// <summary>"there is not enough free disk space (about 2.3 GB are needed)" for a copy to <paramref name="limitBytes"/>.</summary>
    public static Note LowDiskNote(long limitBytes)
        => Note.Of("reason|there is not enough free disk space (about {size} are needed)", ("size", Note.Size(RequiredFreeBytes(limitBytes))));

    /// <summary>
    /// The reason of a failed copy as a note, for "Couldn't make a smaller copy: {reason}". The reason is the note the failure carried
    /// (<paramref name="reason"/>: a catalog entry for what Clink words itself, verbatim text for what Windows or a library said); a
    /// failure that carried none is shown as its English <paramref name="english"/>. The kinds that name their own reason are chosen by
    /// <paramref name="kind"/>, never by the text: low disk (sized by <paramref name="limitBytes"/>) and stayed too big.
    /// </summary>
    public static Note ReasonNote(Note? reason, string english, ShareCopyError kind = ShareCopyError.None, long limitBytes = 0)
    {
        if (kind == ShareCopyError.LowDisk && limitBytes > 0) return LowDiskNote(limitBytes);
        if (kind == ShareCopyError.StayedTooBig) return StayedTooBigReasonNote;
        return reason ?? Note.Verbatim(english);
    }

    /// <summary>"Couldn't make a smaller copy: the disk is full. The original is 46 MB, over Discord free (20 MB)." (<paramref name="reason"/> is shown as it is, or by <paramref name="kind"/>.)</summary>
    public static Note FailedNote(ShareFitTarget target, string reason, long originalBytes, ShareCopyError kind = ShareCopyError.None)
        => FailedNote(target, null, reason, originalBytes, kind);

    /// <summary>The same with the reason as the note the failure carried (null: <paramref name="english"/> is shown as it is).</summary>
    public static Note FailedNote(ShareFitTarget target, Note? reason, string english, long originalBytes, ShareCopyError kind = ShareCopyError.None)
        => Note.Of("Couldn't make a smaller copy: {reason}. The original is {size}, over {limit}.",
            ("reason", ReasonNote(reason, english, kind, target.LimitBytes)), ("size", Note.Size(originalBytes)), ("limit", target.LabelNote));

    public static string FailedText(ShareFitTarget target, string reason, long originalBytes) => FailedNote(target, reason, originalBytes).Shown;

    /// <summary>The copy stayed over the limit through every attempt: <see cref="FailedText"/> with that reason, and the length to trim to when there is one.</summary>
    public static Note StayedTooBigNote(ShareFitTarget target, long originalBytes, int maxSeconds)
        => maxSeconds > 0
            ? Note.Of("Couldn't make a smaller copy: the copy stayed too big. The original is {size}, over {limit}. Trim it to about {trim}.",
                ("size", Note.Size(originalBytes)), ("limit", target.LabelNote), ("trim", Capabilities.FormatClipLength(maxSeconds)))
            : FailedNote(target, StayedTooBigReasonNote, StayedTooBigReason, originalBytes, ShareCopyError.StayedTooBig);

    public static string StayedTooBigText(ShareFitTarget target, long originalBytes, int maxSeconds) => StayedTooBigNote(target, originalBytes, maxSeconds).Shown;

    /// <summary>"Smaller copies are not available while Clink runs as administrator. The original is 46 MB, over Discord free (20 MB)." Each reason is a whole sentence of its own.</summary>
    public static Note UnavailableNote(ShareFitTarget target, ShareUnavailableKind why, long originalBytes)
    {
        var size = ("size", (object?)Note.Size(originalBytes));
        var limit = ("limit", (object?)target.LabelNote);
        return why switch
        {
            ShareUnavailableKind.Elevated => Note.Of("Smaller copies are not available while Clink runs as administrator. The original is {size}, over {limit}.", size, limit),
            ShareUnavailableKind.Redirected => Note.Of("Smaller copies are not available because the share cache folder is redirected somewhere else (a junction or link). The original is {size}, over {limit}.", size, limit),
            ShareUnavailableKind.Linked => Note.Of("Smaller copies are not available because the share cache folder for this copy is a junction or link. The original is {size}, over {limit}.", size, limit),
            _ => Note.Of("Smaller copies are not available. The original is {size}, over {limit}.", size, limit),
        };
    }

    public static string UnavailableText(ShareFitTarget target, ShareUnavailableKind why, long originalBytes) => UnavailableNote(target, why, originalBytes).Shown;

    /// <summary>The English reason of <paramref name="why"/> (data for <see cref="ShareCopyResult.Error"/> and logs; the user is shown <see cref="UnavailableNote"/>).</summary>
    public static string UnavailableReason(ShareUnavailableKind why) => why switch
    {
        ShareUnavailableKind.Elevated => ShareCache.ElevatedReason,
        ShareUnavailableKind.Redirected => ShareCache.RedirectedReason,
        ShareUnavailableKind.Linked => ShareCache.LinkedReason,
        _ => "not available",
    };

    /// <summary>"Too big for Copium: open Clink to make a smaller copy" (a game is in front and copies during games are off).</summary>
    public static Note GameRefusalNote(ShareFitTarget target) => Note.Of("Too big for {label}: open Clink to make a smaller copy", ("label", target.ShortLabelNote));

    public static string GameRefusalText(ShareFitTarget target) => GameRefusalNote(target).Shown;

    /// <summary>"2 files are over Discord free (20 MB): share them one at a time." and, for one, "1 file is over Discord free (20 MB): share it on its own." (null for none).</summary>
    public static string? MultiFileNote(ShareFitTarget target, int overCount) => overCount switch
    {
        <= 0 => null,
        1 => L.T("1 file is over {label}: share it on its own.", ("label", target.Label)),
        _ => L.Plural("{count} files are over {label}: share them one at a time.", overCount, ("label", target.Label)),
    };

    /// <summary>The not-enough-disk reason (English: it is data in <see cref="ShareCopyResult.Error"/>; <see cref="ReasonNote"/> words it for the user): "there is not enough free disk space (about 2.3 GB are needed)".</summary>
    public static string LowDiskReason(long limitBytes) => $"there is not enough free disk space (about {L.BytesIn("en", RequiredFreeBytes(limitBytes))} are needed)";

    /// <summary>The line the window shows for a state, as a note (null for Fits: nothing to say). <paramref name="clipSeconds"/> is the clip's length (for the can't-fit text).</summary>
    public static Note? TextNote(ShareFitState state, ShareFitTarget target, long originalBytes, double clipSeconds) => state.Kind switch
    {
        ShareFitStateKind.Fits => null,
        ShareFitStateKind.Over => OverNote(target, originalBytes),
        ShareFitStateKind.Preparing => Note.Of("Preparing..."),
        ShareFitStateKind.Working => WorkingNote(target, state.Plan, state.Progress),
        ShareFitStateKind.Waiting => Note.Of("Waiting..."),
        ShareFitStateKind.Done => Note.Verbatim(state.Copy is { } c ? DoneDetails(c.Bytes, c.Seconds, c.Label) : ""),
        ShareFitStateKind.CantFit => CantFitNote(target, clipSeconds, state.MaxSeconds),
        ShareFitStateKind.Failed => state.ErrorKind == ShareCopyError.StayedTooBig ? StayedTooBigNote(target, originalBytes, state.MaxSeconds)
            : FailedNote(target, state.ReasonNote, state.Reason ?? "something went wrong", originalBytes, state.ErrorKind),
        ShareFitStateKind.Unavailable => UnavailableNote(target, state.Why, originalBytes),
        ShareFitStateKind.Refused => state.ErrorKind == ShareCopyError.GameInFront || string.IsNullOrEmpty(state.Reason) ? GameRefusalNote(target) : Note.Verbatim(state.Reason),
        _ => null,
    };

    /// <summary>The line the window shows for a state (null for Fits: nothing to say).</summary>
    public static string? Text(ShareFitState state, ShareFitTarget target, long originalBytes, double clipSeconds) => TextNote(state, target, originalBytes, clipSeconds)?.Shown;
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
