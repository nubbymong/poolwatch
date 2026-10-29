using System.Buffers.Binary;
using System.Text;
using PoolWatch.Core;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Util;

namespace PoolWatch.Tests;

/// <summary>Fixes from the independent review of Part 3 (spec and code quality).</summary>
public class ReviewFixesPartThreeTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();

    private static ServiceOption Opt(string name, ServiceStartType start, bool running = false) =>
        new() { Service = new ServiceView { Name = name, StartType = start, Running = running }, Group = ServiceGroup.Optional, Catalogue = Cat.Find(name) };

    private static ServiceChange Rec(string name, ServiceStartType original, ServiceStartType now, bool wasRunning = true) =>
        new() { Service = name, OriginalStart = original, OriginalRunning = wasRunning, NewStart = now, ChangedUtc = T0 };

    [Fact]
    public void RestoreActsOnlyOnAServiceStillAsPoolWatchLeftIt()
    {
        var rec = Rec("SysMain", ServiceStartType.Automatic, ServiceStartType.Disabled);
        ServicePlan.GateRestore(Opt("SysMain", ServiceStartType.Disabled), rec, false);               // as left: restore
        Assert.False(ServicePlan.AlreadyRestored(Opt("SysMain", ServiceStartType.Disabled), rec));
        ServicePlan.GateRestore(Opt("SysMain", ServiceStartType.Automatic, running: true), rec, false); // back already: only the record goes
        Assert.True(ServicePlan.AlreadyRestored(Opt("SysMain", ServiceStartType.Automatic, running: true), rec));
        Assert.False(ServicePlan.AlreadyRestored(Opt("SysMain", ServiceStartType.Automatic, running: false), rec)); // still needs its start
        // Changed since to something else (or a record that never matched this service): refused.
        var ex = Assert.Throws<InvalidOperationException>(() => ServicePlan.GateRestore(Opt("SysMain", ServiceStartType.Manual), rec, false));
        Assert.Contains("was changed since", ex.Message);
        // A forged record that claims an original of Automatic + running for a service PoolWatch never touched (live Manual).
        Assert.Throws<InvalidOperationException>(() => ServicePlan.GateRestore(Opt("Fax", ServiceStartType.Manual),
            Rec("Fax", ServiceStartType.Automatic, ServiceStartType.Disabled), false));
    }

    [Fact]
    public void RestoreToDefaultIsRefusedUnlessTheServiceIsOffOutsidePoolWatch()
    {
        var running = ServicePlan.Options([new ServiceView { Name = "DiagTrack", StartType = ServiceStartType.Automatic, Running = true }], Cat, false, new HashSet<string>());
        Assert.Contains("not off outside PoolWatch", Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildDefault("DiagTrack", running, false, T0)).Message);
        var mine = ServicePlan.Options([new ServiceView { Name = "DiagTrack", StartType = ServiceStartType.Disabled }], Cat, false, new HashSet<string>(),
            [Rec("DiagTrack", ServiceStartType.Automatic, ServiceStartType.Disabled)]);
        Assert.Contains("use Put back", Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildDefault("DiagTrack", mine, false, T0)).Message);
    }

    private const long PerSecond = 10_000_000;

    [Fact]
    public void HeavyAppsDoNotFlickerBetweenMinutePointsAndGoWhenTheyStop()
    {
        var h = new ProcessCostHistory();
        // Points every 65 s (the sampler's 10 s ticks plus jitter), a process at 1 core.
        for (int i = 0; i <= 35; i++)
        {
            var t = T0.AddSeconds(65 * i);
            h.Observe(t, [new ProcessSample { Pid = 100, CreateTime = 7, ImageName = "TrayHelper.exe", KernelTime = PerSecond * 65 * i }], 0);
        }
        var lastPoint = T0.AddSeconds(65 * 35);
        foreach (int s in new[] { 0, 10, 30, 55, 64 }) // asked at any moment between two points
            Assert.Single(h.Heavy(lastPoint.AddSeconds(s), TimeSpan.FromMinutes(30), 0.5));
        Assert.Empty(h.Heavy(lastPoint.AddMinutes(5), TimeSpan.FromMinutes(30), 0.5)); // no fresh point: it is not running now
    }

    [Fact]
    public void PageFaultCountersThatWrapStillGiveTheRate()
    {
        var h = new ProcessCostHistory();
        uint start = uint.MaxValue - 30_000_000; // wraps within the first minute at 1M faults/s
        for (int m = 0; m <= 10; m++)
            h.Observe(T0.AddMinutes(m), [new ProcessSample { Pid = 100, CreateTime = 7, ImageName = "esrv_svc.exe", KernelTime = PerSecond * 60 * m, PageFaultCount = unchecked(start + (uint)(1_000_000L * 60 * m)) }], 0);
        var avg = h.Average(new ProcessKey(100, 7), TimeSpan.FromMinutes(10), T0.AddMinutes(10))!.Value;
        Assert.InRange(avg.faultsPerSec, 999_000, 1_001_000);
    }

    /// <summary>A minimal .lnk: header, optional ID list, LinkInfo with an ANSI local base path, Unicode arguments.</summary>
    private static byte[] Lnk(string target, string? args, bool idList = false)
    {
        var ms = new MemoryStream();
        var header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        new Guid("00021401-0000-0000-C000-000000000046").ToByteArray().CopyTo(header, 4);
        uint flags = 0x2 | 0x80 | (args is null ? 0u : 0x20u) | (idList ? 0x1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), flags);
        ms.Write(header);
        if (idList) { ms.Write([4, 0]); ms.Write([1, 2, 3, 4]); }
        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        int size = 0x1C + basePath.Length + 1;
        var li = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(li, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(4), 0x1C);
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(0x10), 0x1C);
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(0x18), (uint)(0x1C + basePath.Length)); // an empty suffix
        basePath.CopyTo(li, 0x1C);
        ms.Write(li);
        if (args is not null)
        {
            var a = Encoding.Unicode.GetBytes(args);
            ms.Write(BitConverter.GetBytes((ushort)args.Length));
            ms.Write(a);
        }
        return ms.ToArray();
    }

    [Fact]
    public void ShortcutsAreReadWithoutTheShell()
    {
        var l = ShellLink.Read(Lnk(@"C:\Program Files\Vendor\agent.exe", "--logon"));
        Assert.Equal((@"C:\Program Files\Vendor\agent.exe", "--logon"), (l!.Value.Target, l.Value.Arguments));
        Assert.Equal(@"C:\Program Files\Vendor\VendorTray.exe", ShellLink.Read(Lnk(@"C:\Program Files\Vendor\VendorTray.exe", null, idList: true))!.Value.Target);
        Assert.Null(ShellLink.Read(Lnk(@"C:\x.exe", "a")[..60]));            // truncated
        Assert.Null(ShellLink.Read(Encoding.ASCII.GetBytes("not a shortcut at all, just text that is long enough to pass the size check ...")));
        var bad = Lnk(@"C:\x.exe", "a");
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(0x4C + 0x10), 0xFFFFFF);    // a LocalBasePath offset far outside
        Assert.Null(ShellLink.Read(bad));
    }

    [Fact]
    public void TheWindowStaysOpenWhileATraceRuns()
    {
        var closed = new Incident { Id = "x", Dir = "x", Tag = PoolTag.Parse("Reff"), Kind = IncidentKind.Growth, OnsetUtc = T0, LastRiseUtc = T0.AddMinutes(20), ClosedUtc = T0.AddMinutes(30) };
        Assert.Equal(T0.AddMinutes(30), IncidentManager.ProtectWindow([closed]).until);
        Assert.Null(IncidentManager.ProtectWindow([closed], captureRunning: true).until);
        Assert.Equal((null, null), IncidentManager.ProtectWindow([], captureRunning: true));
    }

    [Fact]
    public void EvictionOverTheCapKeepsWindowRecordsCompactAndTheTraceFillsTheirCommandLine()
    {
        var reg = new ProcessRegistry(maxRecords: 300) { KeepShortLived = TimeSpan.FromMinutes(15), ProtectSince = T0.AddMinutes(-10) };
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0);
        var claude = reg.OnStart(4000, 4, T0.AddHours(-1).ToFileTimeUtc(), "claude.exe", null, "claude", null, T0.AddHours(-1));
        for (int i = 0; i < 400; i++)
        {
            var t = T0.AddMilliseconds(i * 100);
            reg.OnStart(10_000 + i, 4000, t.ToFileTimeUtc(), "node.exe", @"C:\nodejs\node.exe", "node vitest.mjs", null, t);
            reg.OnStop(10_000 + i, t.ToFileTimeUtc(), 0, t.AddMilliseconds(50));
        }
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }, new() { Pid = 4000, ParentPid = 4, CreateTime = claude.Key.CreateTime, ImageName = "claude.exe" }], T0.AddMinutes(2));
        Assert.True(reg.CompactCount > 0);                      // over the 300 cap: the oldest window records were compacted, not dropped
        var oldest = reg.FindInstance(10_000, T0.ToFileTimeUtc(), T0)!;
        Assert.Null(oldest.CommandLine);
        // The trace row carries the command line: the identity uses it (node's script), the registry is left as it is.
        var u = new CapturePoolUsage(10_000, "node.exe", PoolTag.Parse("Reff"), 700 << 10, 0, 540, 0, [], T0.ToFileTimeUtc(), @"node C:\x\node_modules\vitest\vitest.mjs", 4000, T0);
        var cap = new CaptureResult { PoolEventsParsed = true, StartUtc = T0, EndUtc = T0.AddMinutes(1), Tags = [u.Tag], Pool = [u] };
        var traced = Assert.Single(TraceAttribution.Resolve(cap, u.Tag, reg));
        Assert.True(traced.Resolved);
        Assert.Contains("vitest", traced.Identity.Display);
        Assert.Equal(OwnerKind.ClaudeSession, traced.Identity.Owner.Kind);
        Assert.Null(reg.FindInstance(10_000, T0.ToFileTimeUtc(), T0)!.CommandLine);
    }

    [Fact]
    public void EndProcessIsNeverOfferedForWindowsOrServiceHosts()
    {
        Assert.Null(EndProcessPolicy.WhyNot(5000, "TrayHelper.exe", []));
        Assert.Null(EndProcessPolicy.WhyNot(5001, "chrome.exe", []));
        Assert.Contains("core Windows or security process", EndProcessPolicy.WhyNot(900, "svchost.exe", [])!);
        Assert.Contains("core Windows or security process", EndProcessPolicy.WhyNot(700, "lsass.exe", [])!);
        Assert.Contains("core Windows or security process", EndProcessPolicy.WhyNot(3000, "MsMpEng.exe", [])!);
        Assert.Contains("turn the service off", EndProcessPolicy.WhyNot(2000, "esrv_svc.exe", ["ESRV_SVC_QUEENCREEK"])!);
        Assert.Contains("virtual machine", EndProcessPolicy.WhyNot(6000, "vmmem", [])!);
        Assert.NotNull(EndProcessPolicy.WhyNot(4, "System", []));
    }
}
