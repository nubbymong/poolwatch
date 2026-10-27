using PoolWatch.Core;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Model;
using PoolWatch.Core.Spawn;

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
            Assert.True(acc.Due(t));
            found.AddRange(acc.Observe(Live(cua[i], other[i], ref pid), Id, t));
            Assert.False(acc.Due(t.AddMinutes(1)));
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
        foreach (var c in new[] { 12, 13 }) found.AddRange(acc.Observe(Live(c, 0, ref pid), Id, T0.AddMinutes(5 * n++)));
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
        for (int i = 0; i < cua.Length; i++) found.AddRange(acc.Observe(Live(cua[i], 0, ref pid), Id, T0.AddMinutes(i)));
        Assert.Empty(found); // 4 -> 1 within the window: helpers do exit
        found.AddRange(acc.Observe(Live(8, 0, ref pid), Id, T0.AddMinutes(7))); // 1,5,6,7,8: steady again
        Assert.Single(found);
    }
}
