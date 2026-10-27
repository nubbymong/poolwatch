using PoolWatch.Core;
using PoolWatch.Core.Cache;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

/// <summary>Addendum 2: a RAMMap "empty standby list" on a busy PC (pool 12.2 -> 7.5 GB, standby 46 GB -> 73 MB) as the reference clear.</summary>
public class ClearCacheTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 30, 0, DateTimeKind.Utc);
    private const long MB = 1L << 20, GB = 1L << 30;
    private static PoolTotals Pool(double pagedGb, double nonPagedGb) => new((ulong)(pagedGb * GB), (ulong)(nonPagedGb * GB), 0, 0);

    private static ClearRecord Clear(ClearMode mode, double freedGb, DateTime t, ClearTrigger trigger = ClearTrigger.Manual) => new()
    {
        TimeUtc = t, Mode = mode, Trigger = trigger, Before = Pool(6.2, 6.0), After = Pool(6.2 - freedGb / 2, 6.0 - freedGb / 2),
        ListsBefore = new MemoryLists(1 * GB, 0, 46 * GB, 300 * MB, 10 * GB), ListsAfter = new MemoryLists(47 * GB, 0, 73 * MB, 300 * MB, 0),
        TagsBefore = [(PoolTag.Parse("Reff"), 722 * MB), (PoolTag.Parse("FMfn"), 737 * MB), (PoolTag.Parse("MmSt"), 1950 * MB)],
        TagsAfter = [(PoolTag.Parse("Reff"), 72 * MB), (PoolTag.Parse("FMfn"), 109 * MB), (PoolTag.Parse("MmSt"), 1496 * MB)],
    };

    [Fact]
    public void ClearSummaryIsTheOwnersRamMapResult()
    {
        var r = Clear(ClearMode.Standby, 4.7, T0);
        string s = r.Summary();
        Assert.StartsWith("Clear cache (standby list, manual): freed 4.7 GB of pool (paged 6.2 GB -> 3.85 GB, non-paged 6.0 GB -> 3.65 GB); standby 46.0 GB -> 73 MB", s);
        Assert.EndsWith("By tag: Reff 722 MB -> 72 MB, FMfn 737 MB -> 109 MB, MmSt 1.9 GB -> 1.46 GB.", s); // most freed first
        var f = r.ToFinding();
        Assert.Equal(FindingKind.CacheClear, f.Kind);
        Assert.InRange((long)f.Data["freedBytes"]!, (long)(4.7 * GB) - (1 << 22), (long)(4.7 * GB) + (1 << 22));
        Assert.Equal("Clear cache (standby list) failed: the purge returned 0xC0000061", new ClearRecord { TimeUtc = T0, Mode = ClearMode.Standby, Trigger = ClearTrigger.Manual, Before = Pool(1, 1), Error = "the purge returned 0xC0000061" }.Summary());
    }

    [Fact]
    public void MemoryListsTellAvailableFromFree()
    {
        var l = new MemoryLists(1 * GB, 512 * MB, 46 * GB, 300 * MB, 10 * GB);
        Assert.Equal("free 1.5 GB, standby (file cache) 46.0 GB, modified 300 MB: Task Manager's \"available\" 47.5 GB is mostly cache", l.Text);
        Assert.False(default(MemoryLists).Known);
    }

    [Fact]
    public void AutomaticRuleTriggersAndRateLimits()
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearPoolGb = 8, AutoClearEveryHours = 1 };
        s.Normalize();
        var start = T0;
        Assert.Null(ClearPolicy.Due(new Settings { AutoClearEnabled = false }, Pool(9, 9), T0, null, start)); // off by default
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s, Pool(5, 4), T0, null, start));
        Assert.Null(ClearPolicy.Due(s, Pool(5, 4), T0.AddMinutes(20), T0, start)); // at most once per 30 min
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s, Pool(5, 4), T0.AddMinutes(31), T0, start));
        Assert.Null(ClearPolicy.Due(s, Pool(2, 2), T0.AddMinutes(31), T0, start));
        Assert.Equal(ClearTrigger.Schedule, ClearPolicy.Due(s, Pool(2, 2), T0.AddMinutes(61), T0, start));
        Assert.Null(ClearPolicy.Due(s, Pool(2, 2), T0.AddMinutes(30), null, start)); // the first scheduled clear waits a period
        var fast = new Settings { AutoClearEnabled = true, AutoClearMinMinutes = 5 };
        fast.Normalize();
        Assert.Equal(30, fast.AutoClearMinMinutes); // never more often than every 30 min
    }

    [Fact]
    public void LowPriorityIsPreferredOnlyWhenItFreesThePool()
    {
        var s = new Settings();
        Assert.Equal(ClearMode.LowPriority, ClearPolicy.ModeFor([], s)); // try it first
        List<ClearRecord> weak = [Clear(ClearMode.LowPriority, 0.1, T0), Clear(ClearMode.Standby, 4.7, T0.AddHours(1))];
        Assert.Equal(ClearMode.Standby, ClearPolicy.ModeFor(weak, s));
        Assert.StartsWith("The low-priority purge frees little", ClearPolicy.LowPriorityVerdict(weak));
        List<ClearRecord> good = [Clear(ClearMode.LowPriority, 4.0, T0), Clear(ClearMode.Standby, 4.7, T0.AddHours(1))];
        Assert.Equal(ClearMode.LowPriority, ClearPolicy.ModeFor(good, s));
        Assert.StartsWith("The low-priority purge alone frees the pool", ClearPolicy.LowPriorityVerdict(good));
        Assert.Equal(ClearMode.Standby, ClearPolicy.ModeFor(good, new Settings { AutoClearMode = "standby" }));
        Assert.Null(ClearPolicy.LowPriorityVerdict([Clear(ClearMode.Standby, 4.7, T0)]));
        List<ClearRecord> lowOnly = [Clear(ClearMode.LowPriority, 4.0, T0)];
        Assert.Equal(ClearMode.Standby, ClearPolicy.ModeFor(lowOnly, s)); // one full purge to compare
        Assert.True(ClearPolicy.IsExperiment(lowOnly, s));
        Assert.False(ClearPolicy.IsExperiment(good, s));
    }

    [Fact]
    public void PoolRuleBacksOffWhenAClearFreedLittle()
    {
        var s = new Settings { AutoClearEnabled = true, AutoClearPoolGb = 8, AutoClearEveryHours = 0 };
        s.Normalize();
        var weak = Clear(ClearMode.Standby, 0.1, T0, ClearTrigger.PoolOverLimit); // 12.2 GB pool, 100 MB freed: not cache-held
        Assert.Null(ClearPolicy.Due(s, Pool(6, 6), T0.AddMinutes(31), T0, T0, weak));
        Assert.Null(ClearPolicy.Due(s, Pool(6, 6), T0.AddHours(5), T0, T0, weak));
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s, Pool(6, 6), T0.AddHours(6.1), T0, T0, weak));
        var good = Clear(ClearMode.Standby, 4.7, T0, ClearTrigger.PoolOverLimit);
        Assert.Equal(ClearTrigger.PoolOverLimit, ClearPolicy.Due(s, Pool(6, 6), T0.AddMinutes(31), T0, T0, good));
    }

    [Fact]
    public void ClearableNowIsThePoolAboveTheLastClearsFloor()
    {
        Assert.Null(ClearPolicy.ClearableNow([], Pool(6, 6)));
        var last = Clear(ClearMode.Standby, 4.7, T0); // left 7.5 GB
        Assert.InRange(ClearPolicy.ClearableNow([last], Pool(4.5, 4.5))!.Value, (long)(1.5 * GB) - (1 << 22), (long)(1.5 * GB) + (1 << 22));
        Assert.Equal(0, ClearPolicy.ClearableNow([last], Pool(3, 3)));
    }

    [Fact]
    public void CpuSummaryNamesTheModulesAndThePool()
    {
        var p = new CpuProfile
        {
            Seconds = 30, Samples = 240_000, Processors = 24,
            TopCpu = [new("user mode", 90_000, 0.375), new("refs.sys", 50_000, 0.21), new("FLTMGR.SYS", 21_000, 0.09), new("idle", 60_000, 0.25)],
            TopWait = [new("refs.sys", 4100, 9000)], TopDpcIsr = [new("ndis.sys", 300, 0)], PoolBytes = 10 * GB,
        };
        Assert.Equal("CPU 22% busy over 30 s; kernel time mostly in refs.sys (21%), FLTMGR.SYS (9%); DPC / ISR: ndis.sys 300 ms; waits: refs.sys 4.1 s (pool 10.0 GB then)", p.Summary());
    }
}
