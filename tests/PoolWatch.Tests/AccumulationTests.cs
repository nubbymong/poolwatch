using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

public class AccumulationTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Owner Codex = new(OwnerKind.App, "app:codex", "Codex");
    private static readonly Owner Code = new(OwnerKind.App, "app:vs code", "VS Code");

    private static List<ProcessRecord> Live(int cua, int other, ref int pid)
    {
        var list = new List<ProcessRecord>();
        for (int i = 0; i < cua; i++) list.Add(new ProcessRecord { Key = new ProcessKey(1000 + i, 1), Image = "node.exe", Label = "cua" });
        for (int i = 0; i < other; i++) list.Add(new ProcessRecord { Key = new ProcessKey(5000 + pid++, 1), Image = "node.exe", Label = "ext" });
        return list;
    }

    private static ProcessIdentity? Id(ProcessRecord r) =>
        r.Label == "cua" ? new ProcessIdentity("node.exe", "Codex cua_node (server.mjs)", Codex, false) : new ProcessIdentity("node.exe", "extensionHost", Code, false);

    [Fact]
    public void FlagsSteadyGrowthNotChurn()
    {
        var acc = new ChildAccumulation(new Settings { AccumulationIntervalSeconds = 300, AccumulationWindowPoints = 6, AccumulationMinCount = 5, AccumulationMinRise = 3 });
        int pid = 0;
        var found = new List<Finding>();
        int[] cua = [1, 2, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        int[] other = [6, 2, 7, 3, 6, 2, 7, 3, 6, 2, 7, 3]; // churn: many, but not piling up
        for (int i = 0; i < cua.Length; i++)
        {
            var t = T0.AddMinutes(5 * i);
            Assert.True(acc.Due(At(t)));
            found.AddRange(acc.Observe(Live(cua[i], other[i], ref pid), Id, At(t)));
            Assert.False(acc.Due(At(t.AddMinutes(1))));
        }
        Assert.Equal(2, found.Count); // at 6 (1,2,3,3,4,5,6: 5 of 6 steps rose), and again at 9 (half as many again)
        var f = found[0];
        Assert.Equal(FindingKind.ChildAccumulation, f.Kind);
        Assert.Contains("9 × node.exe", found[1].Title);
        Assert.StartsWith("Child processes piling up: 6 × node.exe · Codex cua_node (server.mjs) under Codex (1 -> 6 in 30 min", f.Title);
        Assert.Equal(11, acc.Top(1)[0].Current);
        // Next at 13 (9 x 3 / 2), not at 12.
        found.Clear();
        int n = 12;
        foreach (var c in new[] { 12, 13 }) found.AddRange(acc.Observe(Live(c, 0, ref pid), Id, At(T0.AddMinutes(5 * n++))));
        Assert.Single(found);
        Assert.Contains("13 × node.exe", found[0].Title);
    }

    [Fact]
    public void ADropResetsTheTrend()
    {
        var acc = new ChildAccumulation(new Settings { AccumulationIntervalSeconds = 60, AccumulationWindowPoints = 4, AccumulationMinCount = 5, AccumulationMinRise = 3 });
        int pid = 0;
        var found = new List<Finding>();
        int[] cua = [2, 3, 4, 1, 5, 6, 7];
        for (int i = 0; i < cua.Length; i++) found.AddRange(acc.Observe(Live(cua[i], 0, ref pid), Id, At(T0.AddMinutes(i))));
        Assert.Empty(found); // 4 -> 1 within the window: helpers do exit
        found.AddRange(acc.Observe(Live(8, 0, ref pid), Id, At(T0.AddMinutes(7)))); // 1,5,6,7,8: steady again
        Assert.Single(found);
    }

    [Fact]
    public void TheCensusesTheHourlyPointsAndTheSpansAreTimedOnTheClockThatOnlyGoesForward()
    {
        // A census every 5 minutes. The system clock is set back an hour after the first: the next census is still due
        // 5 minutes on, by the forward clock, not an hour and 5 minutes on. Set forward a day: no census before its 5
        // minutes, no hourly point before its 59 minutes, and the span a finding says is the forward clock's.
        var acc = new ChildAccumulation(new Settings { AccumulationIntervalSeconds = 300, AccumulationWindowPoints = 4, AccumulationMinCount = 5, AccumulationMinRise = 3 });
        int pid = 0;
        const long min = 60_000;
        acc.Observe(Live(1, 0, ref pid), Id, At(T0, 0));
        Assert.False(acc.Due(At(T0.AddDays(1), 4 * min))); // set forward: not yet due
        Assert.True(acc.Due(At(T0.AddHours(-1).AddMinutes(5), 5 * min))); // set back: due on time
        acc.Observe(Live(2, 0, ref pid), Id, At(T0.AddHours(-1).AddMinutes(5), 5 * min));
        Assert.Equal(2, acc.Censuses);
        var g = Assert.Single(acc.Groups);
        Assert.Single(g.Hourly); // the hour back is no hour passed
        var found = new List<Finding>();
        for (int i = 2; i <= 5; i++) found.AddRange(acc.Observe(Live(i + 2, 0, ref pid), Id, At(T0.AddDays(1).AddMinutes(5 * i), 5 * i * min)));
        Assert.Equal(6, acc.Censuses);
        Assert.Single(g.Hourly); // the day forward is no hour passed either
        var f = Assert.Single(found);
        Assert.Contains("(1 -> 6 in 20 min,", f.Title); // the forward clock's 20 minutes, not a day
    }

    [Fact]
    public void TheHourBeforeAPileIsFlaggedAgainIsTimedOnTheClockThatOnlyGoesForward()
    {
        // A pile that grows by one at every 5-minute census is flagged once, then again only after an hour (it never grows
        // by another half here). The system clock is set forward a day right after the flag: no flag again 5 minutes on.
        // Then it is set back an hour: the flag again comes an hour after the first, by the forward clock, not a day or
        // an hour off it.
        var acc = new ChildAccumulation(new Settings { AccumulationIntervalSeconds = 300, AccumulationWindowPoints = 4, AccumulationMinCount = 5, AccumulationMinRise = 3 });
        int pid = 0;
        const long min = 60_000;
        var flags = new List<long>();
        for (int i = 0; i <= 18; i++)
        {
            long ms = 5 * i * min;
            var utc = i < 5 ? T0.AddMinutes(5 * i) : i == 5 ? T0.AddDays(1).AddMinutes(5 * i) : T0.AddHours(-1).AddMinutes(5 * i);
            if (acc.Observe(Live(100 + i, 0, ref pid), Id, At(utc, ms)).Count > 0) flags.Add(ms / min);
        }
        Assert.Equal([20L, 80L], flags); // flagged at the fifth census, and again 60 forward minutes on
    }
}
