using PoolWatch.Core;
using PoolWatch.Core.Filters;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

namespace PoolWatch.Tests;

public class FilterTimelineTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 11, 2, 33, DateTimeKind.Utc);

    private static LogEvent Fm(DateTime t, int id, string msg) => new(t, "System", FilterTimeline.FilterManagerProvider, id, msg, []);

    [Fact]
    public void FilterLoadNextToPackageActivationIsFlagged()
    {
        var tl = new FilterTimeline();
        var act = tl.OnLogEvent(new LogEvent(T0, FilterTimeline.AppModelChannel, "Microsoft-Windows-AppModel-Runtime", 201,
            "Created Desktop AppX container for package OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0 with id 1.", []));
        Assert.Equal(FindingKind.PackageActivation, Assert.Single(act).Kind);
        Assert.Equal("OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0", act[0].Image);
        var load = tl.OnLogEvent(Fm(T0.AddSeconds(1), 6, "File System Filter 'bindflt' (Version 10.0, 2026-08-01T00:00:00.000000000Z) has successfully loaded and registered with Filter Manager."));
        Assert.Equal(2, load.Count);
        Assert.Equal("Filter bindflt loaded (FilterManager event 6)", load[0].Title);
        Assert.Equal(FindingSeverity.Warning, load[1].Severity);
        Assert.Contains("bindflt loaded 1 s after package OpenAI.CodexBeta", load[1].Title);
        // The same package again within a minute is folded; a far-away load is not paired.
        Assert.Empty(tl.OnLogEvent(new LogEvent(T0.AddSeconds(20), FilterTimeline.AppModelChannel, "x", 201, "again OpenAI.CodexBeta_26.727.4816.0_x64__2p2nqsd0c76g0", [])));
        Assert.Single(tl.OnLogEvent(Fm(T0.AddMinutes(10), 6, "File System Filter 'wcifs' (Version 10.0) has successfully loaded and registered with Filter Manager.")));
        var unload = Assert.Single(tl.OnLogEvent(Fm(T0.AddMinutes(11), 1, "File System Filter 'wcifs' unloaded successfully.")));
        Assert.Equal("unload", unload.Data["action"]);
        Assert.Equal(1, tl.PackageEvents);
        Assert.Equal(3, tl.FilterEvents);
    }

    [Fact]
    public void PackageNameFromFamilyOrProperties()
    {
        Assert.Equal("OpenAI.Codex_2p2nqsd0c76g0", FilterTimeline.PackageName("Launched OpenAI.Codex_2p2nqsd0c76g0!App"));
        Assert.Null(FilterTimeline.PackageName("nothing here"));
        var tl = new FilterTimeline();
        Assert.Single(tl.OnLogEvent(new LogEvent(T0, FilterTimeline.AppModelChannel, "x", 39, null, ["", "Microsoft.Windows.Photos_2026.1.0.0_x64__8wekyb3d8bbwe"])));
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
