using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Model;
using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// When the automatic clear runs while the kernel pool climbs fast: the size rule's back-off after a clear that freed
/// little ends once the pool is cache-held again, and the urgent clear empties the cache at once while the non-paged pool
/// is over its alert and still rising.
/// <para>
/// The replay is a night on a PC with a ReFS Dev Drive that ended in a hard hang (2026-11-17, about 01:28 UTC), from the
/// numbers PoolWatch measured: its clears (the pool before each and 2 minutes after it) and the non-paged pool at its
/// alerts. Settings as they were: automatic clearing on, every hour or at 8 GB, at most every 30 minutes, auto mode; the
/// non-paged alert at 5 GB and rising 100 MB in 10 minutes. A size-rule clear at 21:19 freed 462 MB of an 8.23 GB pool
/// (under 10%), which held the size rule off for 6 hours, until 03:19. The scheduled clears after it freed 1.99 GB,
/// 5.75 GB and nothing; the pool was over 8 GB again from 00:22, and at 01:05 the non-paged pool alone was 11.18 GB and
/// rising, with no clear until the hourly one at 01:20, which freed 12.49 GB. The PC hung 8 minutes later.
/// </para>
/// Each moment is asked with the clears as they had happened by then.
/// </summary>
public class UrgentClearTests
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly DateTime Evening = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A time of the night: <paramref name="day"/> 0 is 16 November, 1 is 17 November (UTC).</summary>
    private static DateTime T(int day, int h, int m, int s = 0) => Evening.AddDays(day).AddHours(h).AddMinutes(m).AddSeconds(s);

    private static PoolTotals Pool(double pagedGb, double nonPagedGb) => new((ulong)(pagedGb * GB), (ulong)(nonPagedGb * GB), 0, 0);

    /// <summary>The settings in force that night.</summary>
    private static Settings ThatNight(bool urgent = true)
    {
        var s = new Settings
        {
            AutoClearEnabled = true, AutoClearPoolGb = 8, AutoClearEveryHours = 1, AutoClearMinMinutes = 30, AutoClearMode = "auto", AutoClearUrgent = urgent,
            NonPagedTotalAlertBytes = 5 * GB, NonPagedTotalRiseBytesPer10Min = 100 * MB,
        };
        s.Normalize();
        return s;
    }

    private static ClearRecord Clear(DateTime t, ClearTrigger trigger, (double Paged, double NonPaged) before, (double Paged, double NonPaged)? after,
        ClearMode mode = ClearMode.Standby, string? error = null) => new()
    {
        TimeUtc = t, TickMs = At(t).Ms, Mode = mode, Trigger = trigger, Before = Pool(before.Paged, before.NonPaged),
        After = after is { } a ? Pool(a.Paged, a.NonPaged) : null, Error = error,
    };

    /// <summary>The night's clears, each as measured 2 minutes after it ran (paged and non-paged pool in GB, before and after).</summary>
    private static readonly ClearRecord[] Night =
    [
        Clear(T(0, 18, 49, 14), ClearTrigger.Schedule, (3.11, 3.01), (2.37, 2.85)),      // freed 920 MB
        Clear(T(0, 19, 49, 24), ClearTrigger.Schedule, (3.94, 3.30), (3.49, 3.12)),      // freed 640 MB
        Clear(T(0, 20, 49, 34), ClearTrigger.Schedule, (3.55, 3.04), (3.51, 3.06)),      // freed 26 MB
        Clear(T(0, 21, 19, 44), ClearTrigger.PoolOverLimit, (4.84, 3.39), (4.21, 3.57)), // freed 462 MB of 8.23 GB: little, the size rule backs off
        Clear(T(0, 22, 19, 54), ClearTrigger.Schedule, (4.76, 4.62), (3.55, 3.83)),      // freed 1.99 GB
        Clear(T(0, 23, 20, 4), ClearTrigger.Schedule, (7.25, 7.13), (3.50, 5.13)),       // freed 5.75 GB
        Clear(T(1, 0, 20, 9), ClearTrigger.Schedule, (3.84, 4.09), (4.50, 4.78)),        // freed nothing (the pool grew in its 2 minutes)
        Clear(T(1, 1, 20, 19), ClearTrigger.Schedule, (10.12, 11.31), (3.68, 5.27)),     // freed 12.49 GB
    ];

    /// <summary>The clears that had run by <paramref name="t"/> (each was measured long before the next moment asked).</summary>
    private static List<ClearRecord> By(DateTime t) => Night.Where(c => c.TimeUtc < t).ToList();

    /// <summary>When PoolWatch started that day, on the forward clock (the schedule counts from the last clear long before).</summary>
    private static readonly long Started = At(T(0, 8, 0)).Ms;

    /// <summary>What the automatic rule starts at <paramref name="t"/>, with the pool then and the non-paged rise over the last 10 minutes.</summary>
    private static (ClearMode Mode, ClearTrigger Trigger)? Ask(Settings s, DateTime t, PoolTotals pool, long? nonPagedRise) =>
        ClearPolicy.Automatic(By(t), s, pool, At(t), Started, experimentRunning: false, nonPagedRise);

    [Fact]
    public void TheNightReplayedTheSizeRuleClearsAgainOnceThePoolIsCacheHeldAndTheUrgentClearRunsAsTheNonPagedPoolClimbs()
    {
        var s = ThatNight();
        // The 21:19 size-rule clear freed little, and the 22:19 scheduled clear then freed 1.99 GB of 9.38 GB: the pool was
        // cache-held again, so the size rule's back-off is over. The pool is over 8 GB, and the rule waits only for the
        // 30 minutes after the 22:19 clear (paged as measured at 22:22, non-paged at the 22:45 alert).
        Assert.Null(ClearPolicy.LastPoolClear(By(T(0, 22, 49, 54))));
        Assert.Null(Ask(ThatNight(urgent: false), T(0, 22, 49, 50), Pool(3.55, 5.46), null));
        Assert.Equal(ClearTrigger.PoolOverLimit, Ask(ThatNight(urgent: false), T(0, 22, 49, 54), Pool(3.55, 5.46), null)?.Trigger);

        // 22:45: the non-paged pool 5.46 GB, over its 5 GB alert, and up 1.56 GB in 10 minutes. 25 minutes after the last
        // clear: the regular rules wait (30 minutes), the urgent clear does not. Always the full clear.
        Assert.Equal((ClearMode.Standby, ClearTrigger.Urgent), Ask(s, T(0, 22, 45, 34), Pool(3.55, 5.46), 1560 * MB));

        // 00:12: 5.01 GB and up 977 MB in 10 minutes, 52 minutes after the 23:20 clear.
        Assert.Equal((ClearMode.Standby, ClearTrigger.Urgent), Ask(s, T(1, 0, 12, 4), Pool(3.50, 5.01), 977 * MB));

        // 00:23: 6.1 GB and up 183 MB, but the 00:20 clear ran 3 minutes before: no clear (the urgent clear's 10 minutes).
        Assert.Null(Ask(s, T(1, 0, 23, 39), Pool(4.50, 6.10), 183 * MB));

        // 00:50: 30 minutes after the 00:20 clear, with the pool over 8 GB since 00:22 (9.28 GB then, and rising), the size
        // rule clears: the back-off ended with the 22:19 clear. (On the night it waited until 03:19, so nothing ran.)
        var nineGb = Pool(4.50, 4.78);
        Assert.Null(Ask(s, T(1, 0, 50, 0), nineGb, 0));
        Assert.Equal(ClearTrigger.PoolOverLimit, Ask(s, T(1, 0, 50, 9), nineGb, 0)?.Trigger);
        Assert.Equal(ClearTrigger.PoolOverLimit, Ask(s, T(1, 0, 52, 0), nineGb, 0)?.Trigger);
        Assert.Equal(ClearTrigger.PoolOverLimit, Ask(ThatNight(urgent: false), T(1, 0, 52, 0), nineGb, 0)?.Trigger);

        // 01:05: the non-paged pool alone 11.18 GB and up 441 MB in 10 minutes: the urgent clear runs.
        Assert.Equal((ClearMode.Standby, ClearTrigger.Urgent), Ask(s, T(1, 1, 5, 19), Pool(4.50, 11.18), 441 * MB));
        // With the urgent clear turned off, the size rule (no longer backed off) clears then anyway.
        Assert.Equal(ClearTrigger.PoolOverLimit, Ask(ThatNight(urgent: false), T(1, 1, 5, 19), Pool(4.50, 11.18), 441 * MB)?.Trigger);

        // 01:22: 5.76 GB and up 586 MB in 10 minutes, 2 minutes after the 01:20 clear: not yet.
        Assert.Null(Ask(s, T(1, 1, 22, 39), Pool(3.68, 5.76), 586 * MB));
    }

    // ------------------------------------------------------------------ the size rule's back-off

    private static Settings SizeOnly(double gb = 8)
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearPoolGb = gb, AutoClearEveryHours = 0, AutoClearMinMinutes = 30, AutoClearMode = "standby" };
        s.Normalize();
        return s;
    }

    private static readonly DateTime T0 = new(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A size-rule clear at the tests' start that freed 100 MB of an 8.2 GB pool: little, so the size rule backs off.</summary>
    private static ClearRecord Weak() => Clear(T0, ClearTrigger.PoolOverLimit, (4.2, 4.0), (4.15, 3.95));

    [Fact]
    public void TheBackOffHoldsForAPoolThatIsNotCacheHeld()
    {
        var s = SizeOnly();
        var weak = Weak();
        // The pool stays over 8 GB: no clear every 30 minutes for it, for 6 hours.
        foreach (double hours in new[] { 0.6, 1, 3, 5.9 })
            Assert.Null(ClearPolicy.Automatic([weak], s, Pool(4.3, 4.1), At(T0.AddHours(hours)), At(T0.AddHours(-1)).Ms, false));
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Automatic([weak], s, Pool(4.3, 4.1), At(T0.AddHours(6.01)), At(T0.AddHours(-1)).Ms, false)?.Trigger);
    }

    [Theory]
    [InlineData(ClearTrigger.Schedule)]
    [InlineData(ClearTrigger.Manual)]
    [InlineData(ClearTrigger.Urgent)]
    [InlineData(ClearTrigger.CommandLine)]
    [InlineData(ClearTrigger.Escalation)]
    public void TheBackOffEndsWhenALaterClearFreedRealPool(ClearTrigger trigger)
    {
        var s = SizeOnly();
        var later = Clear(T0.AddHours(1), trigger, (5, 5), (3.5, 4.2)); // freed 2.3 GB of 10 GB: the pool is cache-held again
        Assert.Null(ClearPolicy.LastPoolClear([Weak(), later]));
        Assert.Null(ClearPolicy.Automatic([Weak(), later], s, Pool(4.5, 4.5), At(T0.AddHours(1.4)), At(T0).Ms, false));                    // the spacing after it
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Automatic([Weak(), later], s, Pool(4.5, 4.5), At(T0.AddHours(1.51)), At(T0).Ms, false)?.Trigger);
    }

    [Fact]
    public void ALaterClearThatFreedLittleFailedOrIsStillBeingMeasuredDoesNotEndTheBackOff()
    {
        var s = SizeOnly();
        var little = Clear(T0.AddHours(1), ClearTrigger.Schedule, (5, 5), (4.8, 4.9));          // 300 MB of 10 GB
        var failed = Clear(T0.AddHours(1), ClearTrigger.Manual, (5, 5), null, error: "the purge call failed");
        var measuring = Clear(T0.AddHours(1), ClearTrigger.Manual, (5, 5), null);
        foreach (var later in new[] { little, failed, measuring })
        {
            Assert.Equal(T0, ClearPolicy.LastPoolClear([Weak(), later])?.TimeUtc);
            Assert.Null(ClearPolicy.Automatic([Weak(), later], s, Pool(4.5, 4.5), At(T0.AddHours(2)), At(T0).Ms, false));
        }
    }

    [Fact]
    public void TheBackOffEndsWhenThePoolGrowsWellPastWhatTheClearSaw()
    {
        // The weak clear saw 8.2 GB: the back-off ends once the pool is max(2 GB, a quarter of 8.2 GB) = 2.05 GB above it.
        var s = SizeOnly();
        var weak = Weak();
        long saw = weak.PoolBefore;
        long margin = Math.Max(2 * GB, saw / 4);
        Assert.Equal(saw / 4, margin);
        PoolTotals Exactly(long bytes) => new((ulong)(bytes / 2), (ulong)(bytes - bytes / 2), 0, 0);
        var at = At(T0.AddHours(2));
        Assert.Null(ClearPolicy.Automatic([weak], s, Exactly(saw + margin - MB), at, At(T0).Ms, false));
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Automatic([weak], s, Exactly(saw + margin), at, At(T0).Ms, false)?.Trigger);
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s, Exactly(saw + margin), at, weak.TickMs, At(T0).Ms, weak));
        Assert.Null(ClearPolicy.Due(s, Exactly(saw + margin - MB), at, weak.TickMs, At(T0).Ms, weak));
        // A small pool: 2 GB at least (a quarter of 6 GB is 1.5 GB).
        var small = Clear(T0, ClearTrigger.PoolOverLimit, (3, 3), (2.95, 2.95));
        var s6 = SizeOnly(6);
        Assert.Null(ClearPolicy.Due(s6, Exactly(8 * GB - MB), at, small.TickMs, At(T0).Ms, small));
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s6, Exactly(8 * GB), at, small.TickMs, At(T0).Ms, small));
        // Inside the 30 minutes after the clear, the spacing still holds.
        Assert.Null(ClearPolicy.Due(s, Exactly(saw + margin), At(T0.AddMinutes(29)), weak.TickMs, At(T0).Ms, weak));
    }

    // ------------------------------------------------------------------ the urgent clear

    /// <summary>
    /// The urgent clear reads the non-paged pool's rise the detector reads for its non-paged alert, on the same baseline
    /// (each own clear's drop added back), and nothing while an own clear is being measured; the tags whose non-paged part
    /// rose with a pool over its alert are the growth that is part of that rise (the notices).
    /// </summary>
    [Fact]
    public void TheDetectorGivesTheNonPagedRiseAndTheTagsPartOfIt()
    {
        var settings = new Settings { NonPagedTotalAlertBytes = 5 * GB, NonPagedTotalRiseBytesPer10Min = 100 * MB };
        var h = PoolWatch.Core.History.History.ForInterval(10);
        var det = new PoolWatch.Core.Detection.Detector(settings);
        Assert.Null(det.NonPagedRise10Min);
        Assert.Empty(det.NonPagedRisers);
        PoolTag riser = PoolTag.Parse("Abcd"), flat = PoolTag.Parse("Flat");
        Sample At10s(int i)
        {
            var t = T0.AddSeconds(10 * i);
            // The non-paged pool from 4.9 GB, up 5 MB every 10 seconds (300 MB in 10 minutes), all of it one tag's.
            var tags = new List<PoolTagEntry> { new(riser, 0, 0, 0, 1, 0, (ulong)(400 * MB + i * 5 * MB)), new(flat, 0, 0, 0, 1, 0, (ulong)(200 * MB)) };
            return new Sample(t, new PoolTotals(2UL << 30, (ulong)((long)(4.9 * GB) + i * 5 * MB), 0, 0), tags, []) { TickMs = At(t).Ms };
        }
        for (int i = 0; i <= 66; i++)
        {
            h.Add(At10s(i));
            det.Evaluate(h);
            if (i == 10)
            {
                // Under the alert: the rise is read (50 MB so far), and no tag is part of a rise over it.
                Assert.Equal(50 * MB, det.NonPagedRise10Min);
                Assert.Empty(det.NonPagedRisers);
            }
        }
        // 11 minutes on: 5.22 GB, up 300 MB in the last 10 minutes.
        Assert.Equal(300 * MB, det.NonPagedRise10Min);
        Assert.Equal(riser, Assert.Single(det.NonPagedRisers));
        // The urgent clear goes by it.
        var s = Urgent();
        Assert.Equal(ClearTrigger.Urgent, ClearPolicy.Automatic([], s, h.Totals[^1], At(T0.AddSeconds(660)), At(T0.AddHours(-1)).Ms, false, det.NonPagedRise10Min)?.Trigger);
        // While an own clear is being measured, nothing is read.
        var clear = new ClearRecord { TimeUtc = T0.AddSeconds(670), TickMs = At(T0.AddSeconds(670)).Ms, Mode = ClearMode.Standby, Trigger = ClearTrigger.Urgent, Before = h.Totals[^1] };
        h.Add(At10s(67));
        det.Evaluate(h, new PoolWatch.Core.Detection.OwnClears([clear], TimeSpan.FromMinutes(2)));
        Assert.True(det.Holding);
        Assert.Null(det.NonPagedRise10Min);
        Assert.Empty(det.NonPagedRisers);
        Assert.Null(ClearPolicy.Automatic([clear], s, h.Totals[^1], At(T0.AddSeconds(680)), At(T0.AddHours(-1)).Ms, false, det.NonPagedRise10Min));
    }

    /// <summary>Clearing every 4 hours or at 32 GB (so neither rule is due here), the urgent clear on, the alert at 5 GB and 100 MB in 10 minutes.</summary>
    private static Settings Urgent(bool on = true)
    {
        var s = new Settings
        {
            AutoClearEnabled = true, AutoClearPoolGb = 32, AutoClearEveryHours = 4, AutoClearMinMinutes = 30, AutoClearMode = "auto", AutoClearUrgent = on,
            NonPagedTotalAlertBytes = 5 * GB, NonPagedTotalRiseBytesPer10Min = 100 * MB,
        };
        s.Normalize();
        return s;
    }

    private static (ClearMode Mode, ClearTrigger Trigger)? Urgently(IReadOnlyList<ClearRecord> history, DateTime t, PoolTotals pool, long? rise,
        Settings? s = null, bool experiment = false) =>
        ClearPolicy.Automatic(history, s ?? Urgent(), pool, At(t), At(T0.AddHours(-1)).Ms, experiment, rise);

    [Fact]
    public void TheUrgentClearRunsWhenTheNonPagedPoolIsOverItsAlertAndStillRising()
    {
        var over = Pool(3, 5.5);
        Assert.Equal((ClearMode.Standby, ClearTrigger.Urgent), Urgently([], T0, over, 100 * MB));
        Assert.Null(Urgently([], T0, over, 100 * MB - 1));                         // not rising fast enough
        Assert.Null(Urgently([], T0, over, null));                                 // the rise is not known (a clear is being measured)
        Assert.Null(Urgently([], T0, Pool(3, 5), 500 * MB));                       // at the alert, not over it
        Assert.Equal(ClearTrigger.Urgent, Urgently([], T0, new PoolTotals(3UL << 30, (5UL << 30) + 1, 0, 0), 500 * MB)?.Trigger);
        // Only while automatic clearing is on, with the urgent clear on.
        Assert.Null(Urgently([], T0, over, GB, Urgent(on: false)));
        var off = Urgent();
        off.AutoClearEnabled = false;
        Assert.Null(Urgently([], T0, over, GB, off));
        // Never while an experiment runs: a clear would decide its result.
        Assert.Null(Urgently([], T0, over, GB, experiment: true));
        // Always the full clear, even where the regular rules would try the light one first.
        Assert.Equal(ClearMode.LowPriority, ClearPolicy.ModeFor([], Urgent()));
        Assert.Equal(ClearMode.Standby, Urgently([], T0, over, GB)?.Mode);
    }

    [Fact]
    public void TheUrgentClearRunsInsideTheSpacingButNeverWithinTenMinutesOfAClear()
    {
        var over = Pool(3, 6);
        var lastClear = Clear(T0, ClearTrigger.Schedule, (5, 5), (3, 4)); // freed 3 GB
        Assert.Null(Urgently([lastClear], T0.AddMinutes(9).AddSeconds(59), over, GB));
        Assert.Equal(ClearTrigger.Urgent, Urgently([lastClear], T0.AddMinutes(10), over, GB)?.Trigger);
        Assert.Equal(ClearTrigger.Urgent, Urgently([lastClear], T0.AddMinutes(12), over, GB)?.Trigger); // inside the 30 minutes
        // An urgent clear that freed real pool: the next may follow 10 minutes after it, while the pool still climbs.
        var urgent = Clear(T0, ClearTrigger.Urgent, (5, 7), (3, 5.2));
        Assert.Null(Urgently([urgent], T0.AddMinutes(9), over, GB));
        Assert.Equal(ClearTrigger.Urgent, Urgently([urgent], T0.AddMinutes(10), over, GB)?.Trigger);
        // Never while a clear waits for its measurement.
        var measuring = Clear(T0, ClearTrigger.Manual, (5, 5), null);
        Assert.Null(Urgently([measuring], T0.AddMinutes(20), over, GB));
    }

    [Fact]
    public void TheUrgentClearStandsDownAfterOneThatFreedLittleUntilAClearFreesRealPoolOrThePoolGrowsOn()
    {
        // An urgent clear that freed 200 MB of 12 GB: the non-paged growth is not cache-held. No urgent clear every 10
        // minutes for it.
        var little = Clear(T0, ClearTrigger.Urgent, (5, 7), (4.9, 6.9));
        var still = Pool(5, 7.2);
        foreach (int minutes in new[] { 10, 30, 60, 200 }) // the 4-hour schedule is due only at 240
            Assert.Null(Urgently([little], T0.AddMinutes(minutes), still, GB));
        // The pool grows on: max(2 GB, a quarter of the 12 GB it saw) = 3 GB more than it saw.
        Assert.Null(Urgently([little], T0.AddMinutes(30), Pool(5, 10 - 0.01), GB));
        Assert.Equal(ClearTrigger.Urgent, Urgently([little], T0.AddMinutes(30), Pool(5, 10), GB)?.Trigger);
        // A clear after it frees real pool: the pool is cache-held again, and the urgent clear may run 10 minutes after it.
        var helped = Clear(T0.AddMinutes(60), ClearTrigger.Schedule, (5, 7.2), (3, 6));
        Assert.Equal(ClearTrigger.Urgent, Urgently([little, helped], T0.AddMinutes(70), still, GB)?.Trigger);
        // A clear after it that freed little too, or that failed, changes nothing.
        var alsoLittle = Clear(T0.AddMinutes(60), ClearTrigger.Schedule, (5, 7.2), (5, 7.1));
        var failed = Clear(T0.AddMinutes(60), ClearTrigger.Manual, (5, 7.2), null, error: "the purge call failed");
        Assert.Null(Urgently([little, alsoLittle], T0.AddMinutes(70), still, GB));
        Assert.Null(Urgently([little, failed], T0.AddMinutes(70), still, GB));
        // A scheduled clear that freed little does not stand the urgent clear down: only its own result does.
        Assert.Equal(ClearTrigger.Urgent, Urgently([alsoLittle], T0.AddMinutes(70), still, GB)?.Trigger);
    }

    /// <summary>An urgent clear says so: in the log and the timeline (its trigger), on the graph (automatic) and in the Simple view's line.</summary>
    [Fact]
    public void AnUrgentClearSaysItIsUrgent()
    {
        var urgent = Clear(T0, ClearTrigger.Urgent, (5, 7), (3, 5)); // freed 4 GB
        Assert.Equal("urgent: non-paged pool over its limit and rising", urgent.TriggerText);
        Assert.StartsWith("Clear cache (standby list, urgent: non-paged pool over its limit and rising): freed 4.0 GB of pool", urgent.Summary());
        Assert.Equal("Urgent", urgent.ToFinding().Data!["trigger"]);
        Assert.True(urgent.Automatic);
        string hm = T0.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string line = ClearScheduleText.Line(new NextClear(NextClearKind.Scheduled, T0.AddMinutes(50)), [urgent], Pool(3, 5), T0.AddMinutes(10));
        Assert.Equal($"Next cache clear in 40 min. Last clear at {hm} (urgent: the non-paged pool was climbing fast) freed 4.0 GB.", line);
        // And Next says the urgent clear runs now when it is due, as it says the full clear after a light one.
        var s = Urgent();
        var next = ClearPolicy.Next(s, Pool(3, 6), At(T0), null, At(T0.AddHours(-1)).Ms, null, true, false, false, false, urgentDue: true);
        Assert.Equal(new NextClear(NextClearKind.Scheduled, T0), next);
        Assert.True(ClearPolicy.UrgentNow([], s, Pool(3, 6), GB, At(T0)));
        // An experiment holds it as it holds every automatic clear.
        Assert.Equal(NextClearKind.Experiment, ClearPolicy.Next(s, Pool(3, 6), At(T0), null, At(T0.AddHours(-1)).Ms, null, true, false, true, false, urgentDue: true).Kind);
    }
}
