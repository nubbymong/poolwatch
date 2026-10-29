using System.Globalization;
using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>
/// The Simple view's "Next cache clear" line, the full clear right after a light one that freed little, and whether
/// clearing helps on this PC. The escalation tests use the clears seen on a PC with a ReFS Dev Drive: an automatic light
/// clear freed 0 B, and a full one freed 5.11 GB (paged 6.43 -> 3.23 GB, non-paged 5.57 -> 3.67 GB).
/// </summary>
public class ClearScheduleTests
{
    private const long MB = 1L << 20, GB = 1L << 30;
    /// <summary>Local noon, so "today" and "yesterday" hold in any time zone.</summary>
    private static readonly DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();

    private static PoolTotals Pool(double pagedGb, double nonPagedGb) => new((ulong)(pagedGb * GB), (ulong)(nonPagedGb * GB), 0, 0);
    private static string Hm(DateTime utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static Settings Auto(double everyHours = 1, double poolGb = 0, string mode = "auto")
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearEveryHours = everyHours, AutoClearPoolGb = poolGb, AutoClearMode = mode };
        s.Normalize();
        return s;
    }

    private static ClearRecord Light(DateTime t, double freedGb = 0, ClearTrigger trigger = ClearTrigger.Schedule) => new()
    {
        TimeUtc = t, Mode = ClearMode.LowPriority, Trigger = trigger, Before = Pool(6.43, 5.57), After = Pool(6.43 - freedGb / 2, 5.57 - freedGb / 2),
    };

    private static ClearRecord Full(DateTime t, ClearTrigger trigger = ClearTrigger.Escalation, bool measured = true) => new()
    {
        TimeUtc = t, Mode = ClearMode.Standby, Trigger = trigger, Before = Pool(6.43, 5.57), After = measured ? Pool(3.23, 3.67) : null,
        TagsBefore = [(PoolTag.Parse("Reff"), 2800 * MB), (PoolTag.Parse("MSb+"), 900 * MB), (PoolTag.Parse("FMfn"), 700 * MB)],
        TagsAfter = [(PoolTag.Parse("Reff"), 300 * MB), (PoolTag.Parse("MSb+"), 100 * MB), (PoolTag.Parse("FMfn"), 200 * MB)],
    };

    private static NextClear Next(Settings s, PoolTotals pool, DateTime now, DateTime? last = null, ClearRecord? lastPool = null,
        bool? canPurge = true, bool? ownTask = false, bool experiment = false, bool busy = false, bool escalation = false) =>
        ClearPolicy.Next(s, pool, now, last, Now.AddHours(-3), lastPool, canPurge, ownTask, experiment, busy, escalation);

    // ------------------------------------------------------------------ the next clear

    [Fact]
    public void EachSituationHasItsLine()
    {
        Assert.Equal(NextClearKind.Off, Next(new Settings(), Pool(2, 2), Now).Kind);
        Assert.Equal(NextClearKind.Off, Next(Auto(everyHours: 0, poolGb: 0), Pool(2, 2), Now).Kind); // no rule set: nothing will clear
        Assert.Equal(NextClearKind.OwnTask, Next(Auto(), Pool(2, 2), Now, ownTask: true).Kind);
        Assert.Equal(NextClearKind.NotElevated, Next(Auto(), Pool(2, 2), Now, canPurge: false).Kind);
        Assert.Equal(NextClearKind.Experiment, Next(Auto(), Pool(2, 2), Now, experiment: true).Kind);
        Assert.Equal(NextClearKind.Running, Next(new Settings(), Pool(2, 2), Now, busy: true).Kind); // a clear by hand, too
        // The helper's status is not read yet: the schedule is described (the status arrives within seconds).
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now.AddMinutes(20)), Next(Auto(), Pool(2, 2), Now, Now.AddMinutes(-40), canPurge: null, ownTask: null));

        string Line(NextClear n) => ClearScheduleText.Line(n, [], Pool(3.1, 3.1), Now);
        Assert.Equal("Automatic clearing is off. No cache clear yet.", Line(new(NextClearKind.Off)));
        Assert.Equal("Automatic clearing is paused: your own clear task (RAMMap) does this job. No cache clear yet.", Line(new(NextClearKind.OwnTask)));
        Assert.Equal("Automatic clearing needs PoolWatch to run as administrator. No cache clear yet.", Line(new(NextClearKind.NotElevated)));
        Assert.Equal("Automatic clearing waits until the experiment ends. No cache clear yet.", Line(new(NextClearKind.Experiment)));
        Assert.Equal("A cache clear is running now.", Line(new(NextClearKind.Running)));
    }

    [Fact]
    public void TheScheduleCountsFromTheStartThenFromTheLastClear()
    {
        var s = Auto(everyHours: 1);
        Assert.Equal(Now.AddHours(-2), Next(s, Pool(2, 2), Now).AtUtc);                            // started 3 h ago: due now
        Assert.Equal(Now.AddMinutes(23), Next(s, Pool(2, 2), Now, last: Now.AddMinutes(-37)).AtUtc);
        var twice = Auto(everyHours: 0.25);                                                         // every 15 min, but never within 30
        Assert.Equal(Now.AddMinutes(20), Next(twice, Pool(2, 2), Now, last: Now.AddMinutes(-10)).AtUtc);
    }

    [Fact]
    public void TheSizeRuleBringsTheClearForwardOrWaitsForItsBackOff()
    {
        var both = Auto(everyHours: 1, poolGb: 8);
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now.AddMinutes(23), 8 * GB), Next(both, Pool(3, 3), Now, Now.AddMinutes(-37)));
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now), Next(both, Pool(5, 4), Now, Now.AddMinutes(-37)));          // over the limit now
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now.AddMinutes(10)), Next(both, Pool(5, 4), Now, Now.AddMinutes(-20))); // after the spacing

        var sizeOnly = Auto(everyHours: 0, poolGb: 9);
        Assert.Equal(new NextClear(NextClearKind.AtPoolSize, PoolBytes: 9 * GB), Next(sizeOnly, Pool(3.1, 3.1), Now));
        // A size-rule clear 2 h ago freed little: the rule waits 6 h after it.
        var weak = new ClearRecord { TimeUtc = Now.AddHours(-2), Mode = ClearMode.Standby, Trigger = ClearTrigger.PoolOverLimit, Before = Pool(5, 5), After = Pool(4.95, 4.95) };
        var backedOff = Next(sizeOnly, Pool(3.1, 3.1), Now, Now.AddHours(-2), weak);
        Assert.Equal(new NextClear(NextClearKind.AtPoolSize, PoolBytes: 9 * GB, PoolRuleAfterUtc: Now.AddHours(4)), backedOff);
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now.AddHours(4)), Next(sizeOnly, Pool(5, 5), Now, Now.AddHours(-2), weak)); // over it, backed off
        // Backed off past the scheduled time: the size rule cannot bring it forward, so it is not mentioned.
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now.AddMinutes(30)), Next(Auto(everyHours: 2.5, poolGb: 9), Pool(3, 3), Now, Now.AddHours(-2), weak));
    }

    [Fact]
    public void TheLineNeverDisagreesWithTheRuleThatClears()
    {
        // Every combination of rules, last clear and pool size, minute by minute: the line says "now" exactly when the rule fires,
        // and at the time it names, the rule fires.
        var start = Now.AddHours(-3);
        var weak = new ClearRecord { TimeUtc = Now.AddHours(-1), Mode = ClearMode.Standby, Trigger = ClearTrigger.PoolOverLimit, Before = Pool(5, 5), After = Pool(4.9, 4.9) };
        foreach (var s in new[] { Auto(1, 0), Auto(0, 8), Auto(1, 8), Auto(2.5, 8), Auto(0.5, 6) })
        foreach (DateTime? last in new DateTime?[] { null, Now.AddMinutes(-10), Now.AddMinutes(-45), Now.AddHours(-1) })
        foreach (var lastPool in new[] { null, weak })
        foreach (var pool in new[] { Pool(2, 2), Pool(5, 4) })
            for (int m = 0; m <= 400; m += 7)
            {
                var t = Now.AddMinutes(m);
                if (last is null && lastPool is not null) continue;
                var next = ClearPolicy.Next(s, pool, t, last, start, lastPool, true, false, false, false);
                var due = ClearPolicy.Due(s, pool, t, last, start, lastPool);
                string at = $"{s.AutoClearEveryHours} h, {s.AutoClearPoolGb} GB, last {last}, minute {m}";
                Assert.True(next.Kind is NextClearKind.Scheduled or NextClearKind.AtPoolSize, at);
                Assert.Equal(due is not null, next is { Kind: NextClearKind.Scheduled, AtUtc: { } when } && when <= t);
                if (next is { Kind: NextClearKind.Scheduled, AtUtc: { } named } && named > t)
                    Assert.NotNull(ClearPolicy.Due(s, pool, named, last, start, lastPool));
            }
    }

    [Fact]
    public void TheLineSaysWhenInMinutesHoursOrUnderAMinute()
    {
        string In(double minutes, long? limit = null, DateTime? after = null) =>
            ClearScheduleText.Line(new(NextClearKind.Scheduled, Now.AddMinutes(minutes), limit, after), [], Pool(3.1, 3.1), Now);
        Assert.Equal("Next cache clear in 23 min. No cache clear yet.", In(22.4));
        Assert.Equal("Next cache clear in 1 h 20 min. No cache clear yet.", In(80));
        Assert.Equal("Next cache clear in 2 h. No cache clear yet.", In(119.5));
        Assert.Equal("Next cache clear in under a minute. No cache clear yet.", In(0.5));
        Assert.Equal("Next cache clear in under a minute. No cache clear yet.", In(-3)); // overdue: at the next sample
        Assert.Equal("Next cache clear in 23 min (sooner if the kernel pool reaches 8 GB). No cache clear yet.", In(22.4, 8 * GB));
        Assert.Equal($"Next cache clear in 2 h (sooner if the kernel pool reaches 7.5 GB after {Hm(Now.AddMinutes(30))}). No cache clear yet.",
            In(120, (long)(7.5 * GB), Now.AddMinutes(30)));
        Assert.Equal("Next cache clear when the kernel pool reaches 9 GB (now 6.2 GB). No cache clear yet.",
            ClearScheduleText.Line(new(NextClearKind.AtPoolSize, PoolBytes: 9 * GB), [], Pool(3.1, 3.1), Now));
        Assert.Equal($"Next cache clear when the kernel pool reaches 9 GB (now 6.2 GB), after {Hm(Now.AddHours(3))}. No cache clear yet.",
            ClearScheduleText.Line(new(NextClearKind.AtPoolSize, PoolBytes: 9 * GB, PoolRuleAfterUtc: Now.AddHours(3)), [], Pool(3.1, 3.1), Now));
        var tomorrow = new DateTime(2026, 10, 29, 3, 20, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.EndsWith("(now 6.2 GB), after 03:20 tomorrow. No cache clear yet.",
            ClearScheduleText.Line(new(NextClearKind.AtPoolSize, PoolBytes: 9 * GB, PoolRuleAfterUtc: tomorrow), [], Pool(3.1, 3.1), Now));
        Assert.Equal("The next cache clear is in 12 min.", ClearScheduleText.Context(new(NextClearKind.Scheduled, Now.AddMinutes(12)), Now));
        Assert.Null(ClearScheduleText.Context(new(NextClearKind.AtPoolSize, PoolBytes: 9 * GB), Now));
    }

    [Fact]
    public void TheLastClearIsSaidWithWhatItFreedNeverANegativeAmount()
    {
        var next = new NextClear(NextClearKind.Off);
        var t = Now.AddMinutes(-48);
        string Last(params ClearRecord[] history) => ClearScheduleText.Line(next, history, Pool(3, 3), Now)["Automatic clearing is off. ".Length..];

        Assert.Equal($"Last clear at {Hm(t)} freed 5.1 GB, mostly ReFS file records.", Last(Full(t, ClearTrigger.Manual)));
        var grew = new ClearRecord { TimeUtc = t, Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = Pool(3, 3), After = Pool(3.1, 3) };
        Assert.Equal($"Last clear at {Hm(t)} freed nothing.", Last(grew));                       // the pool grew during the 2 minutes
        var failed = new ClearRecord { TimeUtc = t, Mode = ClearMode.Standby, Trigger = ClearTrigger.Schedule, Before = Pool(3, 3), Error = "the purge returned 0xC0000061" };
        Assert.Equal($"Last clear at {Hm(t)} did not run: the purge returned 0xC0000061.", Last(failed));
        var yesterday = new DateTime(2026, 10, 27, 9, 12, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("Last clear yesterday at 09:12 freed 5.1 GB, mostly ReFS file records.", Last(Full(yesterday, ClearTrigger.Schedule)));
        var older = new DateTime(2026, 10, 26, 9, 12, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.StartsWith("Last clear on 26 Oct at 09:12 freed", Last(Full(older, ClearTrigger.Schedule)));
        // While one runs, the line says so and then what the one before it did.
        Assert.Equal($"A cache clear is running now. Last clear at {Hm(t)} freed 5.1 GB, mostly ReFS file records.",
            ClearScheduleText.Line(new(NextClearKind.Running), [Full(t, ClearTrigger.Manual), Full(Now, ClearTrigger.Manual, measured: false)], Pool(3, 3), Now));
    }

    [Fact]
    public void AFullClearAfterAUselessLightOneIsSaidAsThePair()
    {
        var light = Light(Now.AddMinutes(-30));
        var full = Full(Now.AddMinutes(-27));
        string line = ClearScheduleText.Line(new(NextClearKind.Scheduled, Now.AddMinutes(30)), [light, full], Pool(3.23, 3.67), Now);
        Assert.Equal($"Next cache clear in 30 min. Last clear at {Hm(light.TimeUtc)} (light) freed nothing, so PoolWatch ran a full clear at {Hm(full.TimeUtc)}: it freed 5.1 GB.", line);
        Assert.Contains("(light) freed only 300 MB, so", ClearScheduleText.Line(new(NextClearKind.Off), [Light(Now.AddMinutes(-30), 300.0 / 1024), full], Pool(3, 3), Now));
    }

    [Fact]
    public void MarksAndResultsSayWhatEachClearDid()
    {
        var t = Now.AddMinutes(-90);
        Assert.Equal($"Cache clear at {Hm(t)} (automatic): freed 4.7 GB", ClearScheduleText.Mark(t, (long)(4.7 * GB), automatic: true, light: false, done: true));
        Assert.Equal($"Cache clear at {Hm(t)} (automatic, light): freed nothing", ClearScheduleText.Mark(t, 0, true, true, true));
        Assert.Equal($"Cache clear at {Hm(t)}: being measured", ClearScheduleText.Mark(t, 0, false, false, false));
        Assert.Equal($"The cache clear at {Hm(t)} freed 1.3 GB.", ClearScheduleText.Result(t, (long)(1.3 * GB), done: true));
        Assert.Equal($"The cache clear at {Hm(t)} is being measured.", ClearScheduleText.Result(t, 0, done: false));
        Assert.Equal($"Cache clear at {Hm(t)}: freed nothing", ClearScheduleText.Mark(t, -40 * MB, false, false, true));
        // On the 24-hour graph, a clear before today says its day, as the approved mockup does.
        var yesterday = Now.AddDays(-1);
        Assert.Equal($"Cache clear yesterday at {Hm(yesterday)} (automatic): freed 1.2 GB", ClearScheduleText.Mark(yesterday, (long)(1.2 * GB), true, false, true, Now));
        Assert.Equal($"Cache clear at {Hm(t)} (automatic): freed 4.7 GB", ClearScheduleText.Mark(t, (long)(4.7 * GB), true, false, true, Now));
    }

    // ------------------------------------------------------------------ each automatic rule on its own

    [Fact]
    public void TheScheduleAloneClearsOnTheHourAndNeverForTheSize()
    {
        // 0 GB: no size rule. A pool far over any size never clears; the hours do, and the line says only the schedule.
        var s = Auto(everyHours: 1, poolGb: 0);
        var huge = Pool(40, 20);
        Assert.Null(ClearPolicy.Due(s, huge, Now.AddMinutes(59), Now, Now));
        Assert.Equal(ClearTrigger.Schedule, ClearPolicy.Due(s, huge, Now.AddMinutes(61), Now, Now));
        var next = Next(s, huge, Now, last: Now.AddMinutes(-20));
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now.AddMinutes(40)), next);
        Assert.StartsWith("Next cache clear in 40 min. ", ClearScheduleText.Line(next, [], huge, Now));
        Assert.DoesNotContain("sooner", ClearScheduleText.Line(next, [], huge, Now));
    }

    [Fact]
    public void TheSizeRuleAloneClearsOnlyWhenThePoolIsOverItSize()
    {
        // 0 hours: no schedule. However long since the last clear, only the size clears; the line says the size.
        var s = Auto(everyHours: 0, poolGb: 8);
        Assert.Null(ClearPolicy.Due(s, Pool(3, 2), Now.AddHours(30), Now, Now));
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s, Pool(5, 4), Now.AddHours(1), Now, Now));
        var next = Next(s, Pool(3, 2), Now, last: Now.AddHours(-30));
        Assert.Equal(NextClearKind.AtPoolSize, next.Kind);
        Assert.Equal(8L * GB, next.PoolBytes);
        Assert.StartsWith("Next cache clear when the kernel pool reaches 8 GB (now 5.0 GB).", ClearScheduleText.Line(next, [], Pool(3, 2), Now));
    }

    // ------------------------------------------------------------------ a full clear at once after a useless light one

    [Fact]
    public void AUselessLightClearIsFollowedByAFullOneOnceRightAfterItsMeasurement()
    {
        var s = Auto();
        var light = Light(Now.AddMinutes(-2));                                          // automatic, measured, freed 0 B
        Assert.True(ClearPolicy.EscalateNow([light], s, Now));
        Assert.Equal(ClearMode.Standby, ClearPolicy.ModeFor([light], s));                // and the mode rule picks the full purge
        // The full purge runs: it is the last clear now, so there is no second escalation, and from then on the full purge is used.
        var full = Full(Now);
        Assert.False(ClearPolicy.EscalateNow([light, full], s, Now.AddMinutes(3)));
        Assert.Equal(ClearMode.Standby, ClearPolicy.ModeFor([light, full], s));
        Assert.InRange(full.Freed / (double)GB, 5.09, 5.12);

        Assert.False(ClearPolicy.EscalateNow([Light(Now.AddMinutes(-2), freedGb: 4)], s, Now));                       // it helped
        Assert.False(ClearPolicy.EscalateNow([Light(Now.AddMinutes(-2), trigger: ClearTrigger.Manual)], s, Now));       // not automatic
        Assert.False(ClearPolicy.EscalateNow([light], Auto(mode: "lowpriority"), Now));                       // the user's choice
        Assert.False(ClearPolicy.EscalateNow([light], Auto(mode: "standby"), Now));
        Assert.False(ClearPolicy.EscalateNow([Light(Now.AddMinutes(-31))], s, Now));                                     // past the spacing: the usual rule
        Assert.False(ClearPolicy.EscalateNow([new ClearRecord { TimeUtc = Now, Mode = ClearMode.LowPriority, Trigger = ClearTrigger.Schedule, Before = Pool(6, 6) }], s, Now)); // not measured
        Assert.False(ClearPolicy.EscalateNow([], s, Now));
        // The line says it: the full clear is due at the next sample.
        Assert.Equal(new NextClear(NextClearKind.Scheduled, Now), Next(s, Pool(6.43, 5.57), Now, light.TimeUtc, escalation: true));

        // Where the last full purge freed little too, a light clear that freed little is not followed by it again.
        Assert.False(ClearPolicy.EscalateNow(NeitherHelps(), s, Now));
        Assert.Equal(ClearMode.LowPriority, ClearPolicy.ModeFor(NeitherHelps(), s));

        // Where light clears used to free 2 GB and the full one 3 GB, the first light clear that frees nothing is followed
        // by the full one at once, while the averages over every clear still favour the light clear.
        var used = new List<ClearRecord> { Light(Now.AddHours(-23), freedGb: 2), FreedThree(Now.AddHours(-22)) };
        used.AddRange(Enumerable.Range(0, 19).Select(i => Light(Now.AddHours(-21 + i), freedGb: 2)));
        used.Add(Light(Now.AddMinutes(-2)));
        Assert.Equal(ClearMode.LowPriority, ClearPolicy.ModeFor(used, s));
        Assert.True(ClearPolicy.EscalateNow(used, s, Now));

        static ClearRecord FreedThree(DateTime t) => new()
        {
            TimeUtc = t, Mode = ClearMode.Standby, Trigger = ClearTrigger.Experiment, Before = Pool(6.43, 5.57), After = Pool(4.93, 4.07),
        };
    }

    /// <summary>A PC where no clear helps: a light clear, the full one after it and the next light one each freed about 100 MB.</summary>
    private static ClearRecord[] NeitherHelps() =>
    [
        Light(Now.AddMinutes(-62), freedGb: 0.1),
        new ClearRecord { TimeUtc = Now.AddMinutes(-60), Mode = ClearMode.Standby, Trigger = ClearTrigger.Escalation, Before = Pool(6.43, 5.57), After = Pool(6.37, 5.51) },
        Light(Now.AddMinutes(-2), freedGb: 0.1),
    ];

    [Fact]
    public void TheAutomaticRuleRunsTheFullClearAfterAUselessLightOneThenKeepsTheSchedule()
    {
        var s = Auto();
        var since = Now.AddHours(-3);
        var light = Light(Now.AddMinutes(-2));
        Assert.Equal((ClearMode.Standby, ClearTrigger.Escalation), ClearPolicy.Automatic([light], s, Pool(6.43, 5.57), Now, since, experimentRunning: false));
        Assert.Null(ClearPolicy.Automatic([light], s, Pool(6.43, 5.57), Now, since, experimentRunning: true));
        var off = Auto();
        off.AutoClearEnabled = false;
        Assert.Null(ClearPolicy.Automatic([light], off, Pool(6.43, 5.57), Now, since, false));

        // After the full one: nothing within the spacing, then the hourly clear, as a full purge.
        var full = Full(Now);
        Assert.Null(ClearPolicy.Automatic([light, full], s, Pool(3.23, 3.67), Now.AddMinutes(3), since, false));
        Assert.Equal((ClearMode.Standby, ClearTrigger.Schedule), ClearPolicy.Automatic([light, full], s, Pool(4, 4), Now.AddMinutes(61), since, false));

        // Where no clear helps, the hourly clear stays the light one, with no full purge after it.
        Assert.Null(ClearPolicy.Automatic(NeitherHelps(), s, Pool(6.4, 5.5), Now, since, false));
        Assert.Equal((ClearMode.LowPriority, ClearTrigger.Schedule), ClearPolicy.Automatic(NeitherHelps(), s, Pool(6.4, 5.5), Now.AddMinutes(59), since, false));

        // The first automatic clear is a light one; the next, the full purge it is compared with.
        Assert.Equal((ClearMode.LowPriority, ClearTrigger.Schedule), ClearPolicy.Automatic([], s, Pool(4, 4), Now, since, false));
        Assert.Equal((ClearMode.Standby, ClearTrigger.Experiment), ClearPolicy.Automatic([Light(Now.AddHours(-1), freedGb: 4)], s, Pool(4, 4), Now, since, false));

        // A size-rule clear that freed little holds the size rule off for 6 h, and a later scheduled clear does not hide it.
        var sizeOnly = Auto(everyHours: 0, poolGb: 8);
        var weak = new ClearRecord { TimeUtc = Now.AddMinutes(-40), Mode = ClearMode.Standby, Trigger = ClearTrigger.PoolOverLimit, Before = Pool(6, 6), After = Pool(5.95, 5.95) };
        Assert.Null(ClearPolicy.Automatic([weak], sizeOnly, Pool(6, 6), Now, since, false));
        Assert.Equal((ClearMode.LowPriority, ClearTrigger.PoolOverLimit), ClearPolicy.Automatic([weak], sizeOnly, Pool(6, 6), Now.AddHours(6), since, false));
        Assert.Same(weak, ClearPolicy.LastPoolClear([weak, Light(Now.AddMinutes(-5))]));
    }

    // ------------------------------------------------------------------ PoolWatch's own impact

    [Fact]
    public void AChangeIsMeasuredFromTheLevelPoolWatchsOwnClearLeft()
    {
        var settle = TimeSpan.FromMinutes(2);
        var tenMinutesAgo = Now.AddMinutes(-10);
        Assert.Equal(tenMinutesAgo, ClearPolicy.ChangeFrom(tenMinutesAgo, Now, [], settle));
        // A clear 6 minutes ago: its drop is its result, so the change counts from its 2-minute measurement on.
        var clear = Full(Now.AddMinutes(-6), ClearTrigger.Schedule);
        Assert.Equal(Now.AddMinutes(-4), ClearPolicy.ChangeFrom(tenMinutesAgo, Now, [clear], settle));
        Assert.Equal(tenMinutesAgo, ClearPolicy.ChangeFrom(tenMinutesAgo, Now, [Full(Now.AddMinutes(-20), ClearTrigger.Schedule)], settle)); // before the span
        // A clear just before the span still drops the pool inside it, until its own measurement point.
        Assert.Equal(Now.AddMinutes(-9), ClearPolicy.ChangeFrom(tenMinutesAgo, Now, [Full(Now.AddMinutes(-11), ClearTrigger.Schedule)], settle));
        // Being measured: no change can be told yet.
        Assert.Null(ClearPolicy.ChangeFrom(tenMinutesAgo, Now, [Full(Now.AddMinutes(-1), ClearTrigger.Manual, measured: false)], settle));
        // The latest of two clears counts; one that did not run changes nothing.
        Assert.Equal(Now.AddMinutes(-1), ClearPolicy.ChangeFrom(Now.AddHours(-1), Now, [Full(Now.AddMinutes(-40), ClearTrigger.Schedule), Full(Now.AddMinutes(-3), ClearTrigger.Manual)], settle));
        var failed = new ClearRecord { TimeUtc = Now.AddMinutes(-5), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = Pool(3, 3), Error = "the purge returned 0xC0000061" };
        Assert.Equal(tenMinutesAgo, ClearPolicy.ChangeFrom(tenMinutesAgo, Now, [failed], settle));
    }

    // ------------------------------------------------------------------ does clearing help on this PC

    [Fact]
    public void EvidenceComesFromTheMeasuredClearsAndNamesWhoseRecordsWereFreed()
    {
        Assert.Equal(new ClearEvidence(null, null), ClearEvidence.From([]));
        Assert.Null(ClearEvidence.From([Light(Now.AddHours(-1))]).Helps);                         // a useless light clear proves nothing

        var full = Full(Now.AddMinutes(-30), ClearTrigger.Manual);
        var helps = ClearEvidence.From([Light(Now.AddHours(-1)), full]);
        Assert.Equal((true, full), (helps.Helps, helps.Cited));
        Assert.Equal("On this PC, clearing the cache freed 5.1 GB of the kernel pool, mostly ReFS file records.", helps.Sentence);

        var ntfs = new ClearRecord
        {
            TimeUtc = Now.AddMinutes(-5), Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = Pool(2, 1), After = Pool(1.9, 1),
            TagsBefore = [(PoolTag.Parse("Ntff"), 150 * MB)], TagsAfter = [(PoolTag.Parse("Ntff"), 80 * MB)],
        };
        var little = ClearEvidence.From([ntfs]);
        Assert.False(little.Helps);
        Assert.Equal("On this PC, clearing the cache freed only 102 MB of the kernel pool, so an hourly clear is not needed now. " +
                     "PoolWatch looks again each time the cache is cleared.", little.Sentence);
        Assert.Equal(FileRecords.NTFS, ClearEvidence.MostlyFrom(ntfs));
        Assert.True(ClearEvidence.From([full, ntfs]).Helps);                                      // one clear that helped is enough

        var mixed = Full(Now.AddMinutes(-30), ClearTrigger.Manual);
        mixed.TagsAfter = [(PoolTag.Parse("Reff"), 2500 * MB), (PoolTag.Parse("MSb+"), 900 * MB), (PoolTag.Parse("FMfn"), 200 * MB)];
        Assert.Null(ClearEvidence.MostlyFrom(mixed));                                              // no kind freed half of it
        Assert.Equal("On this PC, clearing the cache freed 5.1 GB of the kernel pool.", new ClearEvidence(true, mixed).Sentence);
    }
}
