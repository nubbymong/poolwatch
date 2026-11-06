using PoolWatch.Core;
using PoolWatch.Core.Filters;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

public class FilterTimelineTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 11, 2, 33, DateTimeKind.Utc);

    private static LogEvent Fm(DateTime t, int id, string msg) => new(t, "System", FilterTimeline.FilterManagerProvider, id, msg, []);

    /// <summary>A record read at its own time on both clocks (the system clock left alone).</summary>
    private static List<Finding> Read(FilterTimeline tl, LogEvent e) => tl.OnLogEvent(e, Clocks.At(e.TimeUtc));

    [Fact]
    public void FilterLoadNextToPackageActivationIsFlagged()
    {
        var tl = new FilterTimeline();
        var act = Read(tl, new LogEvent(T0, FilterTimeline.AppModelChannel, "Microsoft-Windows-AppModel-Runtime", 201,
            "Created Desktop AppX container for package OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0 with id 1.", []));
        Assert.Equal(FindingKind.PackageActivation, Assert.Single(act).Kind);
        Assert.Equal("OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0", act[0].Image);
        var load = Read(tl, Fm(T0.AddSeconds(1), 6, "File System Filter 'bindflt' (Version 10.0, 2026-08-01T00:00:00.000000000Z) has successfully loaded and registered with Filter Manager."));
        Assert.Equal(2, load.Count);
        Assert.Equal("Filter bindflt loaded (FilterManager event 6)", load[0].Title);
        Assert.Equal(FindingSeverity.Warning, load[1].Severity);
        Assert.Contains("bindflt loaded 1 s after package OpenAI.CodexBeta", load[1].Title);
        // The same package again within a minute is folded; a far-away load is not paired.
        Assert.Empty(Read(tl, new LogEvent(T0.AddSeconds(20), FilterTimeline.AppModelChannel, "x", 201, "again OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0", [])));
        Assert.Single(Read(tl, Fm(T0.AddMinutes(10), 6, "File System Filter 'wcifs' (Version 10.0) has successfully loaded and registered with Filter Manager.")));
        var unload = Assert.Single(Read(tl, Fm(T0.AddMinutes(11), 1, "File System Filter 'wcifs' unloaded successfully.")));
        Assert.Equal("unload", unload.Data["action"]);
        Assert.Equal(1, tl.PackageEvents);
        Assert.Equal(3, tl.FilterEvents);
    }

    private const string Codex = "Created Desktop AppX container for package OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0 with id 1.";
    private const string Bindflt = "File System Filter 'bindflt' (Version 10.0) has successfully loaded and registered with Filter Manager.";

    /// <summary>A record stamped <paramref name="utc"/> by the system clock and read onto the forward clock at <paramref name="ms"/>.</summary>
    private static LogEvent Read(LogEvent e, DateTime utc, long ms) => e with { TimeUtc = utc, Ms = ms };

    [Fact]
    public void AFilterLoadIsPairedWithAnActivationOnTheForwardClock()
    {
        var act = new LogEvent(T0, FilterTimeline.AppModelChannel, "x", 201, Codex, []);
        var load = Fm(T0, 6, Bindflt);
        // The pairing warnings of the second record, the first read at the forward clock's 1,000,000 with the system clock
        // at the test's start.
        List<string> Warned(LogEvent first, LogEvent second, DateTime secondUtc, long secondMs)
        {
            var tl = new FilterTimeline();
            tl.OnLogEvent(Read(first, T0, 1_000_000), At(T0, 1_000_000));
            return tl.OnLogEvent(Read(second, secondUtc, secondMs), At(secondUtc, secondMs)).Where(f => f.Severity == FindingSeverity.Warning).Select(f => f.Title).ToList();
        }
        string Paired(string order) => $"Filter bindflt loaded 2 s {order} package OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0 was activated";
        // The system clock set back an hour, then the second record 2 s on the forward clock: paired, either way round (by
        // their times of day they lie an hour apart).
        Assert.Equal([Paired("after")], Warned(act, load, T0.AddSeconds(2) - TimeSpan.FromHours(1), 1_002_000));
        Assert.Equal([Paired("before")], Warned(load, act, T0.AddSeconds(2) - TimeSpan.FromHours(1), 1_002_000));
        // Set forward a day: paired as well.
        Assert.Equal([Paired("after")], Warned(act, load, T0.AddDays(1).AddSeconds(2), 1_002_000));
        // Set back 4 s, the second record 6 s on the forward clock: not paired (by their times of day 2 s apart).
        Assert.Empty(Warned(act, load, T0.AddSeconds(2), 1_006_000));
        Assert.Empty(Warned(load, act, T0.AddSeconds(2), 1_006_000));
    }

    [Fact]
    public void RecordsDrainedAtOneSampleArePairedByWhenEachArrivedOnTheForwardClock()
    {
        // A package activation arrives at the forward clock's 1,000,000 (the test's start by the system clock); the system
        // clock is set; a bindflt load arrives 2 s later on the forward clock. The sampler drains both at its next sample,
        // 5 s after the activation: each is timed by when it arrived, so they are paired, the load 2 s after the activation.
        // Read onto the forward clock at the sample by their times of day, the activation would lie a day before the load,
        // with the clock set forward a day, and they would not be paired; with the clock set back an hour, the activation's
        // time of day, later than the sample's, would be read as the sample, and the load would lie 3 s before it.
        List<string> Warned(TimeSpan set)
        {
            var tl = new FilterTimeline();
            var act = Read(new LogEvent(T0, FilterTimeline.AppModelChannel, "x", 201, Codex, []), T0, 1_000_000);
            var load = Read(Fm(T0, 6, Bindflt), T0.AddSeconds(2) + set, 1_002_000);
            var sample = At(T0.AddSeconds(5) + set, 1_005_000);
            return tl.OnLogEvent(act, sample).Concat(tl.OnLogEvent(load, sample)).Where(f => f.Severity == FindingSeverity.Warning).Select(f => f.Title).ToList();
        }
        const string Paired = "Filter bindflt loaded 2 s after package OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0 was activated";
        Assert.Equal([Paired], Warned(TimeSpan.FromDays(1)));
        Assert.Equal([Paired], Warned(-TimeSpan.FromHours(1)));
    }

    [Fact]
    public void APackageIsEnteredOnceAMinuteAndKeptForPairingTwoMinutesOnTheForwardClock()
    {
        var tl = new FilterTimeline();
        var act = new LogEvent(T0, FilterTimeline.AppModelChannel, "x", 201, Codex, []);
        var other = new LogEvent(T0, FilterTimeline.AppModelChannel, "x", 201, "Created Desktop AppX container for package Contoso.App_1.0.0.0_x64__8wekyb3d8bbwe with id 2.", []);
        Assert.Single(tl.OnLogEvent(Read(act, T0, 0), At(T0, 0)));
        // Set forward a day 30 s on: the same package within its minute is folded.
        Assert.Empty(tl.OnLogEvent(Read(act, T0.AddDays(1).AddSeconds(30), 30_000), At(T0.AddDays(1).AddSeconds(30), 30_000)));
        // Set back two days 61 s on: past its minute, entered again.
        Assert.Single(tl.OnLogEvent(Read(act, T0.AddDays(-1).AddSeconds(61), 61_000), At(T0.AddDays(-1).AddSeconds(61), 61_000)));
        Assert.Equal(2, tl.RecentCount);
        // Another package 2 minutes 30 s on the forward clock after the first, with the system clock set back a week: the
        // first entry (more than two minutes old) is let go, the second (89 s old) kept, and the new one is kept.
        Assert.Single(tl.OnLogEvent(Read(other, T0.AddDays(-7), 150_000), At(T0.AddDays(-7), 150_000)));
        Assert.Equal(2, tl.RecentCount);
    }

    [Fact]
    public void PackageNameFromFamilyOrProperties()
    {
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", FilterTimeline.PackageName("Launched OpenAI.Codex_2p2nqsd0c76g0!App"));
        Assert.Null(FilterTimeline.PackageName("nothing here"));
        var tl = new FilterTimeline();
        Assert.Single(Read(tl, new LogEvent(T0, FilterTimeline.AppModelChannel, "x", 39, null, ["", "Microsoft.Windows.Photos_2026.1.0.0_x64__8wekyb3d8bbwe"])));
    }

    [Fact]
    public void ContainerProcessesAndServices()
    {
        var reg = new ProcessRegistry();
        reg.OnStart(900, 4, T0.ToFileTimeUtc(), "vmcompute.exe", null, null, null, T0);
        var tl = new FilterTimeline();
        var first = tl.OnLifecycle([], reg.LiveRecords, T0);
        Assert.Contains("vmcompute (PID 900)", Assert.Single(first).Title);
        var ev = new LifecycleEvent { TimeUtc = T0.AddMinutes(1), Kind = LifecycleKind.Start, Pid = 901, Image = "vmmemCmZygote" };
        var ex = new LifecycleEvent { TimeUtc = T0.AddMinutes(2), Kind = LifecycleKind.Exit, Pid = 901, Image = "vmmemCmZygote", ExitCode = 0 };
        var other = new LifecycleEvent { TimeUtc = T0.AddMinutes(2), Kind = LifecycleKind.Start, Pid = 5, Image = "notepad.exe" };
        var f = tl.OnLifecycle([ev, ex, other], () => [], T0.AddMinutes(2));
        Assert.Equal(["vmmemCmZygote (PID 901) started", "vmmemCmZygote (PID 901) exited, code 0"], f.Select(x => x.Title));

        var s0 = tl.OnServices(new Dictionary<string, bool> { ["vmcompute"] = true, ["bindflt"] = true }, T0);
        Assert.Contains("CoworkVMService not running", Assert.Single(s0).Title);
        Assert.Empty(tl.OnServices(new Dictionary<string, bool> { ["vmcompute"] = true, ["bindflt"] = true }, T0.AddMinutes(1)));
        var s2 = tl.OnServices(new Dictionary<string, bool> { ["vmcompute"] = false, ["bindflt"] = true, ["CoworkVMService"] = true }, T0.AddMinutes(2));
        Assert.Equal(["Service vmcompute stopped", "Service CoworkVMService started"], s2.Select(x => x.Title).OrderByDescending(x => x.Contains("stopped")).ToList());
    }

    [Fact]
    public void InstanceSnapshotsReportChanges()
    {
        var tl = new FilterTimeline();
        Assert.Contains("skipped (not elevated)", Assert.Single(tl.OnInstances(null, "not elevated", T0)).Title);
        Assert.Empty(tl.OnInstances(null, "not elevated", T0.AddHours(1))); // reported once per run, not hourly
        List<FilterInstance> a = [new("wcifs", @"C:\", "189900", "wcifs Instance"), new("FileInfo", @"C:\", "40500", "FileInfo")];
        var first = Assert.Single(tl.OnInstances(a, null, T0));
        Assert.Contains("FileInfo ×1, wcifs ×1", first.Title);
        Assert.Empty(tl.OnInstances(a, null, T0.AddHours(1)));
        List<FilterInstance> b = [.. a, new("wcifs", @"D:\", "189900", "wcifs Instance"), new("wcifs", @"E:\", "189900", "wcifs Instance")];
        var changed = Assert.Single(tl.OnInstances(b, null, T0.AddHours(2)));
        Assert.Contains(@"+wcifs on D:\", changed.Title);
        Assert.Equal(3, ((Dictionary<string, object?>)changed.Data["perFilter"]!)["wcifs"]);
    }

    [Fact]
    public void NewTagsAreBuiltIn()
    {
        foreach (var (t, owner) in new[] { ("NtFC", "ntfs.sys"), ("IoNm", "nt!io"), ("FMfc", "fltmgr.sys"), ("FMwi", "fltmgr.sys"), ("WCsc", "wcifs.sys"), ("WCfc", "wcifs.sys"),
                     ("WCri", "wcifs.sys"), ("WCse", "wcifs.sys"), ("WCss", "wcifs.sys") })
            Assert.Equal(owner, BuiltinTags.Table[PoolTag.Parse(t)].Owner);
        var k = new TagKnowledge(null, null);
        Assert.True(k.IsFileSystemTag(PoolTag.Parse("WCsc")));
        Assert.True(k.IsFileSystemTag(PoolTag.Parse("IoNm")));
        Assert.NotEqual(PoolTag.Parse("NtFC"), PoolTag.Parse("NtfC")); // case-sensitive
    }
}
