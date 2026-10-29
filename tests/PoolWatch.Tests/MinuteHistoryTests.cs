using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.History;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

/// <summary>
/// The one-minute history is kept for 25 hours. It records the processes that did I/O in each minute, which is all the
/// since-boot report attributes growth by, and not every short command a sample happened to catch idle.
/// </summary>
public class MinuteHistoryTests
{
    /// <summary>Three hours of <paramref name="busy"/> busy processes and a growing tag; with <paramref name="idlePerSample"/> short commands seen once each.</summary>
    private static World ThreeHours(int idlePerSample, int busy = 5)
    {
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        w.AddProcess(4, 0, "System");
        for (int p = 0; p < busy; p++) w.AddProcess(100 + p, 4, $"busy{p}.exe");
        var rnd = new Random(9);
        long tag = 100 * Settings.MB;
        int nextPid = 10_000;
        for (int i = 0; i < 3 * 360; i++)
        {
            for (int p = 0; p < busy; p++) w.Io(100 + p, other: (p + 1) * 200 + rnd.Next(100));
            var idle = Enumerable.Range(0, idlePerSample).Select(_ => w.AddProcess(nextPid++, 100, "git.exe", startIndex: w.Index).Pid).ToList();
            tag += 50_000 + rnd.Next(20_000);
            w.Sample(("Reff", tag));
            foreach (int pid in idle) w.Exit(pid);
        }
        return w;
    }

    private static string SinceBoot(World w) =>
        SinceBootReport.ToMarkdown(SinceBootReport.Build(w.History, w.Registry, new TagKnowledge(null, null)), null, w.History.Minutes[0].TimeUtc, w.Now);

    [Fact]
    public void AMinuteRecordsOnlyTheProcessesThatDidIO()
    {
        var w = ThreeHours(idlePerSample: 20);
        Assert.True(w.History.Minutes.Count >= 170);
        Assert.All(w.History.Minutes, m =>
        {
            Assert.All(m.ProcessOps, ops => Assert.True(ops > 0));
            Assert.InRange(m.Processes.Length, 1, 5); // the busy ones, not the 120 short commands each minute caught
        });
    }

    [Fact]
    public void TheSinceBootAttributionIsTheSameWithOrWithoutIdleShortCommands()
    {
        var quiet = ThreeHours(idlePerSample: 0);
        var churn = ThreeHours(idlePerSample: 20);
        Assert.Contains("busy4.exe (PID 104)", SinceBoot(quiet));
        Assert.Equal(SinceBoot(quiet), SinceBoot(churn));
    }

    /// <summary>
    /// The one difference: an hour in which fewer than three processes did I/O lists only those, where it used to fill
    /// the list with short commands a sample caught idle, each "0%" of the growth.
    /// </summary>
    [Fact]
    public void AnHourWithFewBusyProcessesNamesNoIdleShortCommand()
    {
        var hours = SinceBoot(ThreeHours(idlePerSample: 20, busy: 1)).Split('\n').Where(l => l.StartsWith("- ") && l.Contains("(PID ")).ToList();
        Assert.True(hours.Count >= 3);
        Assert.All(hours, l => Assert.Equal("busy0.exe (PID 100)", Assert.Single(Regex.Matches(l, @"[\w. ]+ \(PID \d+\)")).Value.Trim()));
    }

    /// <summary>
    /// A full 25 hours at 1,000 process starts a minute (900 short commands seen idle once, 100 busy for three samples)
    /// beside 50 busy long-lived processes: each minute holds the 50 and the short commands busy in it, never the idle ones.
    /// </summary>
    [Fact]
    public void TwentyFiveHoursOfChurnHoldOnlyTheBusyProcesses()
    {
        var h = History.ForInterval(10, rawMinutes: 10); // a short raw window: the minute history does not depend on it
        var t0 = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var live = new Dictionary<int, (ProcessSample p, int left)>();
        for (int pid = 100; pid < 150; pid++) live[pid] = (new ProcessSample { Pid = pid, ParentPid = 4, ImageName = "app.exe", CreateTime = 1 }, int.MaxValue);
        int nextPid = 10_000;
        var samples = (int)(TimeSpan.FromHours(26) / TimeSpan.FromSeconds(10));
        for (int i = 0; i < samples; i++)
        {
            var now = t0.AddSeconds(10 * i);
            for (int k = 0; k < 167; k++)
            {
                int pid = nextPid++;
                live[pid] = (new ProcessSample { Pid = pid, ParentPid = 100, ImageName = "bash.exe", CreateTime = now.AddSeconds(-1).ToFileTimeUtc() }, k < 17 ? 3 : 1);
            }
            foreach (var (pid, (p, left)) in live.ToList())
            {
                bool busy = left > 1;
                if (busy) live[pid] = (p with { OtherOperationCount = p.OtherOperationCount + 100 }, left == int.MaxValue ? left : left - 1);
            }
            h.Add(new Sample(now, new PoolTotals(1UL << 30, 1UL << 30, 1UL << 30, 1UL << 30), [new(PoolTag.Parse("Reff"), 1, 0, 64UL << 20, 0, 0, 0)],
                live.Values.Select(v => v.p).ToList()));
            foreach (var pid in live.Where(kv => kv.Value.left == 1).Select(kv => kv.Key).ToList()) live.Remove(pid);
        }
        Assert.InRange(h.Minutes.Count, 1490, 1501); // 25 hours of minutes: the history is full
        long entries = h.Minutes.Sum(m => (long)m.Processes.Length);
        Assert.All(h.Minutes, m => Assert.InRange(m.Processes.Length, 50, 300)); // 1,050 processes are seen each minute
        Assert.True(entries * 24 < 12 << 20, $"{entries:N0} entries of 24 bytes"); // 50 MB when every new process was kept
    }
}
