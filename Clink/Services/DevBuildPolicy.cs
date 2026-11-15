namespace Clink.Services;

/// <summary>
/// Pure rules around the Dev Build channel, kept free of UI and network so the unit tests can pin them: how often the background loop
/// checks (<see cref="Interval"/>) and when the hidden Dev Build box shows (<see cref="BoxVisible"/>, <see cref="SecretClickCounter"/>).
/// </summary>
internal static class DevBuildPolicy
{
    /// <summary>Stable channel: a check every 6 hours (unchanged).</summary>
    internal static readonly TimeSpan StableInterval = TimeSpan.FromHours(6);
    /// <summary>Dev channel: a check every 2 minutes (before jitter).</summary>
    internal static readonly TimeSpan DevInterval = TimeSpan.FromMinutes(2);
    /// <summary>Longest wait after failed dev checks, so a feed that is down is not hammered.</summary>
    internal static readonly TimeSpan DevMaxBackoff = TimeSpan.FromMinutes(15);
    /// <summary>The dev interval varies by up to this fraction either way (so many installs do not hit the feed in step).</summary>
    internal const double JitterFraction = 0.1;

    /// <summary>
    /// How long to wait after a check pass before the next one. Stable is always 6 h (failures included, as before). Dev is 2 min,
    /// doubling per failed pass in a row (4, 8, 15 min cap), with +/-10 % jitter from <paramref name="jitter01"/> (0..1, 0.5 = none),
    /// never above the 15 min cap.
    /// </summary>
    internal static TimeSpan Interval(bool dev, int failedInARow, double jitter01)
    {
        if (!dev) return StableInterval;
        int doublings = Math.Clamp(failedInARow, 0, 10);
        var wait = TimeSpan.FromTicks(Math.Min(DevInterval.Ticks << doublings, DevMaxBackoff.Ticks));
        double j = Math.Clamp(jitter01, 0, 1);
        wait = TimeSpan.FromTicks((long)(wait.Ticks * (1 + JitterFraction * 2 * (j - 0.5))));
        return wait > DevMaxBackoff ? DevMaxBackoff : wait;
    }

    /// <summary>The failed-passes-in-a-row count after a pass: one more when the pass failed (a failed download too), back to 0 when it did not. Capped so it cannot overflow.</summary>
    internal static int NextFailures(int failedInARow, bool passFailed) => passFailed ? Math.Min(failedInARow + 1, 10) : 0;

    /// <summary>How often the background loop looks at the clock: finer on the dev channel so its 2 min interval is kept.</summary>
    internal static TimeSpan LoopTick(bool dev) => dev ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1);

    /// <summary>The Dev Build box shows once revealed this session, and always while it is ticked (a dev user can always get back to releases).</summary>
    internal static bool BoxVisible(bool revealedThisSession, bool useDevBuilds) => revealedThisSession || useDevBuilds;
}

/// <summary>Counts clicks that follow each other closely: <see cref="Required"/> in a row, each within <see cref="MaxGap"/> of the previous one.</summary>
internal sealed class SecretClickCounter
{
    internal const int Required = 10;
    internal static readonly TimeSpan MaxGap = TimeSpan.FromMilliseconds(1500);

    private DateTime _last = DateTime.MinValue;
    private int _count;

    /// <summary>Registers a click at <paramref name="now"/>; true on the click that completes the sequence (the count then starts over).</summary>
    public bool Click(DateTime now)
    {
        _count = _count > 0 && now - _last <= MaxGap && now >= _last ? _count + 1 : 1;
        _last = now;
        if (_count < Required) return false;
        _count = 0;
        return true;
    }
}
