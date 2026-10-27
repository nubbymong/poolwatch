using PoolWatch.Core.Incidents;

namespace PoolWatch.Analysis;

/// <summary>
/// The sampler tick's step for the held-back capture (C1): the newest request that could not start is started once the
/// governor allows it (the hourly cap, not while saturated or typing, never while another runs), or dropped when it is no
/// longer wanted. The governor decides; this only acts on its answer, and is its own unit so a test can hold it to that
/// (review round 2, item 12).
/// </summary>
internal static class PendingCaptures
{
    /// <param name="stillWanted">False drops the request (its incidents have all closed).</param>
    /// <param name="start">Starts the trace the governor allowed.</param>
    /// <param name="dropped">Tells the owner a held-back trace will not run (the governor's reason).</param>
    /// <returns>The governor's decision, or null when nothing was pending or it must still wait.</returns>
    public static CaptureDecision? Tick(CaptureGovernor governor, CaptureConditions conditions, Func<CaptureRequest, bool>? stillWanted,
        Action<CaptureDecision> start, Action<CaptureDecision> dropped)
    {
        if (governor.Pending is null) return null;
        if (governor.TryPending(conditions, stillWanted) is not { } d) return null; // the governor says: not yet
        if (d.Start) start(d);
        else dropped(d);
        return d;
    }
}
