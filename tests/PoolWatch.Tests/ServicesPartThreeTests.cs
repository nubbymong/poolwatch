using PoolWatch.Core.Model;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>Review Part 3, addendum 4 B: own changes, Restore to Windows default, restart on Restore, the Cost column, "saved about N cores".</summary>
public class ServicesPartThreeTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 11, 40, 0, DateTimeKind.Utc);
    private static readonly ServiceCatalogue Cat = DefaultCatalogue.Load();
    private static readonly IReadOnlySet<string> NoDefaults = new HashSet<string>();

    private static ServiceView V(string name, ServiceStartType start = ServiceStartType.Automatic, bool ms = false, bool running = true, bool win32 = true, int pid = 0) =>
        new() { Name = name, DisplayName = name, StartType = start, Running = running, MicrosoftSigned = ms, IsWin32 = win32, ProcessId = pid };

    private static ServiceChange Off(string name, ServiceStartType original = ServiceStartType.Automatic, bool wasRunning = true, string via = "manual") =>
        new() { Service = name, OriginalStart = original, OriginalRunning = wasRunning, NewStart = ServiceStartType.Disabled, ChangedUtc = T0, Via = via };

    [Fact]
    public void PoolWatchsOwnChangeNeverReadsChangedOutsidePoolWatch()
    {
        var services = new[] { V("SysMain", ServiceStartType.Disabled, running: false), V("DiagTrack", ServiceStartType.Disabled, running: false) };
        var opts = ServicePlan.Options(services, Cat, insiderEnrolled: false, NoDefaults, [Off("SysMain")]);
        var sysmain = opts.Single(o => o.Service.Name == "SysMain");
        Assert.Null(sysmain.OffOutsidePoolWatch);                          // PoolWatch did it
        Assert.NotNull(sysmain.PoolWatchChange);
        Assert.StartsWith("off (turned off by PoolWatch ", ServicePlan.StateText(sysmain));
        Assert.False(sysmain.CanRestoreDefault);                           // Restore applies, not "Windows default"
        var diag = opts.Single(o => o.Service.Name == "DiagTrack");
        Assert.Equal(ServicePlan.OffOutside, diag.OffOutsidePoolWatch);    // the owner's own choice
        Assert.Equal(ServicePlan.OffOutside, ServicePlan.StateText(diag));
    }

    [Fact]
    public void AServiceSwitchedBackOnAfterPoolWatchTurnedItOffSaysSo()
    {
        var opts = ServicePlan.Options([V("SysMain", ServiceStartType.Automatic, running: true)], Cat, false, NoDefaults, [Off("SysMain")]);
        Assert.Contains("then switched back on outside it", ServicePlan.StateText(opts[0]));
        Assert.StartsWith("running", ServicePlan.StateText(opts[0]));
    }

    [Fact]
    public void AServiceApplySetToManualReadsAsPoolWatchLeftItRunningOrNot()
    {
        // edgeupdate's entry turns it off to Manual, and Apply does not stop it: running is how Apply left it.
        var manual = new ServiceChange
        {
            Service = "edgeupdate", OriginalStart = ServiceStartType.Automatic, OriginalRunning = true, NewStart = ServiceStartType.Manual, ChangedUtc = T0, Via = "manual",
        };
        string when = T0.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"set to Manual, running (turned off by PoolWatch {when})",
            ServicePlan.StateText(ServicePlan.Options([V("edgeupdate", ServiceStartType.Manual, running: true)], Cat, false, NoDefaults, [manual])[0]));
        Assert.Equal($"set to Manual, stopped (turned off by PoolWatch {when})",
            ServicePlan.StateText(ServicePlan.Options([V("edgeupdate", ServiceStartType.Manual, running: false)], Cat, false, NoDefaults, [manual])[0]));
        // Set back to Automatic outside PoolWatch: switched back on.
        Assert.Equal($"running (turned off by PoolWatch {when}, then switched back on outside it)",
            ServicePlan.StateText(ServicePlan.Options([V("edgeupdate", ServiceStartType.Automatic, running: true)], Cat, false, NoDefaults, [manual])[0]));
        // A service Apply disabled that runs again was switched back on outside PoolWatch, as before.
        Assert.Equal($"running (turned off by PoolWatch {when}, then switched back on outside it)",
            ServicePlan.StateText(ServicePlan.Options([V("SysMain", ServiceStartType.Disabled, running: true)], Cat, false, NoDefaults, [Off("SysMain")])[0]));
    }

    [Fact]
    public void CatalogueKnowsWindowsDefaultsOnlyWhereItSaysSo()
    {
        Assert.Equal(ServiceStartType.Automatic, Cat.WindowsDefault("DiagTrack"));
        Assert.Equal(ServiceStartType.Manual, Cat.WindowsDefault("InventorySvc"));
        Assert.Equal(ServiceStartType.Automatic, Cat.WindowsDefault("SysMain"));
        Assert.Equal(ServiceStartType.Manual, Cat.WindowsDefault("edgeupdatem")); // per service inside one entry
        Assert.Equal(ServiceStartType.Automatic, Cat.WindowsDefault("edgeupdate"));
        Assert.Null(Cat.WindowsDefault("ESRV_SVC_QUEENCREEK"));                   // a vendor service has no Windows default
        Assert.Null(Cat.WindowsDefault("NotCatalogued"));
        // A default of disabled / boot / garbage is never one PoolWatch writes.
        var odd = ServiceCatalogue.Load("""[{ "services": ["A", "B", "C"], "name": "x", "category": "y", "windowsDefault": { "A": "disabled", "B": "boot", "C": "auto" } }]""");
        Assert.Null(odd.WindowsDefault("A"));
        Assert.Null(odd.WindowsDefault("B"));
        Assert.Equal(ServiceStartType.Automatic, odd.WindowsDefault("c"));        // names match ignoring case
    }

    [Fact]
    public void RestoreToWindowsDefaultGoesThroughTheGateAndSavesTheOwnersSetting()
    {
        var services = new[]
        {
            V("DiagTrack", ServiceStartType.Disabled, running: false),
            V("ESRV_SVC_QUEENCREEK", ServiceStartType.Disabled, running: false),
            V("WinDefend", ServiceStartType.Disabled, ms: true, running: false),
            V("someDriver", ServiceStartType.Disabled, running: false, win32: false),
        };
        var opts = ServicePlan.Options(services, Cat, insiderEnrolled: false, NoDefaults);
        Assert.True(opts.Single(o => o.Service.Name == "DiagTrack").CanRestoreDefault);
        Assert.False(opts.Single(o => o.Service.Name == "ESRV_SVC_QUEENCREEK").CanRestoreDefault); // default unknown
        Assert.False(opts.Single(o => o.Service.Name == "WinDefend").CanRestoreDefault);           // protected

        var op = ServicePlan.BuildDefault("diagtrack", opts, insiderEnrolled: false, T0);
        Assert.Equal(ServiceStartType.Automatic, op.NewStart);
        Assert.Equal(ServiceStartType.Disabled, op.Record.OriginalStart);   // the owner's choice, for Restore
        Assert.False(op.Record.OriginalRunning);
        Assert.Equal(ServicePlan.ViaWindowsDefault, op.Record.Via);
        Assert.Equal(ServiceStartType.Disabled, ServicePlan.RestoreTarget(op.Record));
        Assert.False(ServicePlan.StartAfterRestore(op.Record));             // putting Disabled back never starts it

        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildDefault("ESRV_SVC_QUEENCREEK", opts, false, T0));
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildDefault("WinDefend", opts, false, T0));
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildDefault("someDriver", opts, false, T0));
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildDefault("NotInstalled", opts, false, T0));
    }

    [Fact]
    public void ADefaultFromATamperedCatalogueStillMeetsTheHardGate()
    {
        // Even a catalogue that names a protected service with a default cannot get it past the gate.
        var rogue = ServiceCatalogue.Load("""[{ "services": ["WinDefend"], "name": "x", "category": "y", "windowsDefault": { "WinDefend": "manual" } }]""");
        var opts = ServicePlan.Options([V("WinDefend", ServiceStartType.Disabled, ms: true, running: false)], rogue, false, NoDefaults);
        Assert.False(opts[0].CanRestoreDefault);
        Assert.Throws<InvalidOperationException>(() => ServicePlan.BuildDefault("WinDefend", opts, false, T0));
    }

    /// <summary>
    /// Turning off, a service comes after those that depend on it; putting back, after those it depends on, also through a
    /// service that is not listed (C depends on B, which depends on A). Otherwise the order is as given, and a loop of
    /// dependencies, which Windows refuses, keeps its own order.
    /// </summary>
    [Fact]
    public void ServicesAreOrderedByWhatTheyDependOn()
    {
        var deps = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = ["B"], ["B"] = ["A"], ["Loop1"] = ["Loop2"], ["Loop2"] = ["Loop1"],
        };
        IReadOnlyList<string> DependsOn(string s) => deps.TryGetValue(s, out var d) ? d : [];
        List<string> Order(bool dependentsFirst, params string[] items) => ServicePlan.InDependencyOrder(items, s => s, DependsOn, dependentsFirst);

        Assert.Equal(["X", "C", "A", "Y"], Order(dependentsFirst: true, "X", "A", "C", "Y"));
        Assert.Equal(["X", "A", "C", "Y"], Order(dependentsFirst: false, "X", "C", "A", "Y"));
        Assert.Equal(["X", "A", "C", "Y"], Order(dependentsFirst: false, "X", "A", "C", "Y"));
        Assert.Equal(["X", "Loop1", "Loop2"], Order(dependentsFirst: true, "Loop1", "Loop2", "X"));
        Assert.Equal(["Loop2", "Loop1"], Order(dependentsFirst: false, "Loop2", "Loop1"));
        Assert.Empty(Order(dependentsFirst: true));
    }

    [Fact]
    public void RestoreStartsAServiceThatWasRunning()
    {
        Assert.True(ServicePlan.StartAfterRestore(Off("SysMain", ServiceStartType.Automatic, wasRunning: true)));
        Assert.True(ServicePlan.StartAfterRestore(Off("WerSvc", ServiceStartType.Manual, wasRunning: true)));
        Assert.False(ServicePlan.StartAfterRestore(Off("SysMain", ServiceStartType.Automatic, wasRunning: false)));
        Assert.False(ServicePlan.StartAfterRestore(Off("X", ServiceStartType.Disabled, wasRunning: true)));
    }

    [Fact]
    public void CostColumnUsesTheServicesProcessAndNamesASharedHost()
    {
        var services = new[]
        {
            V("ESRV_SVC_QUEENCREEK", pid: 2000),
            V("BrokerA", pid: 900), V("BrokerB", pid: 900), V("BrokerC", pid: 900), // one svchost
            V("Stopped", ServiceStartType.Manual, running: false),
        };
        var filled = ServiceCosts.Fill(services, pid => pid switch { 2000 => (1.7, 887_000), 900 => (0.3, 0), _ => null });
        var esrv = filled.Single(s => s.Name == "ESRV_SVC_QUEENCREEK");
        Assert.Equal(1.7, esrv.Cores);
        Assert.Equal(0, esrv.SharedWith);
        Assert.Equal("1.7 cores, 887k page faults/s", ServicePlan.CostText(esrv));
        var broker = filled.Single(s => s.Name == "BrokerA");
        Assert.Equal(2, broker.SharedWith);
        Assert.Equal("0.3 core (host shared with 2 other services)", ServicePlan.CostText(broker));
        Assert.Equal("not running", ServicePlan.CostText(filled.Single(s => s.Name == "Stopped")));
        Assert.Equal("idle", ServicePlan.Cost(0.01, 10));
        Assert.Equal(0.1, ServiceCosts.Share((0.3, 300), 3).cores, 6);
        Assert.Equal(100, ServiceCosts.Share((0.3, 300), 3).faultsPerSec);
    }

    [Fact]
    public void SavedAboutNCoresOverTheHourAfter()
    {
        var s = new ServiceSavings();
        s.Start("ESRV_SVC_QUEENCREEK", T0, beforeCores: 1.7, beforeFaultsPerSec: 887_000);
        Assert.StartsWith("measuring what it saves", ServiceSavings.Text(s.Get("esrv_svc_queencreek")!, T0));
        var done = new List<ServiceSavings.Entry>();
        for (int sec = 10; sec <= 3600; sec += 10) done.AddRange(s.Observe(T0.AddSeconds(sec), _ => (0, 0))); // stopped: costs nothing
        var e = Assert.Single(done);                                   // reported once, at the hour
        Assert.Empty(s.Observe(T0.AddSeconds(3700), _ => (0, 0)));
        Assert.Equal(1.7, e.SavedCores, 3);
        Assert.Equal(3600, e.Seconds, 0);
        string text = ServiceSavings.Text(e, T0.AddHours(1));
        Assert.StartsWith("saved about 1.7 cores and 887k page faults/s over the hour after", text);
    }

    [Fact]
    public void ASelfRestartingServiceSavesLessAndAGapIsNotCounted()
    {
        var s = new ServiceSavings();
        s.Start("ESRV_SVC_QUEENCREEK", T0, 1.7, 0);
        // Off for 30 min, then it restarts itself (as esrv_svc did) and costs 1.7 cores again.
        for (int sec = 10; sec <= 1800; sec += 10) s.Observe(T0.AddSeconds(sec), _ => (0, 0));
        // A 10-minute sampler gap (sleep): not counted at either cost.
        for (int sec = 2400; sec <= 3600; sec += 10) s.Observe(T0.AddSeconds(sec), _ => (1.7, 0));
        var e = s.Get("ESRV_SVC_QUEENCREEK")!;
        Assert.Equal(1800 + 1200, e.Seconds, 0);
        Assert.Equal(1.7 * 1200 / 3000, e.AfterCores, 3);
        Assert.Contains("saved about 1.0 core", ServiceSavings.Text(e, T0.AddHours(1)));
        s.Stop("ESRV_SVC_QUEENCREEK");                                  // Restore: nothing more to measure
        Assert.Null(s.Get("ESRV_SVC_QUEENCREEK"));
    }

    [Fact]
    public void AnIdleServiceSavesNothingMeasurable()
    {
        var s = new ServiceSavings();
        s.Start("Fax", T0, 0, 0);
        for (int sec = 10; sec <= 300; sec += 10) s.Observe(T0.AddSeconds(sec), _ => (0, 0));
        Assert.StartsWith("no measurable saving", ServiceSavings.Text(s.Get("Fax")!, T0.AddMinutes(5)));
    }
}

/// <summary>Review Part 3: per-process cost over minutes (a change's "before"; heavy background apps over 30 min).</summary>
public class ProcessCostHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 4, 44, 0, DateTimeKind.Utc);
    private const long PerSecond = 10_000_000; // 100 ns units

    private static ProcessSample P(int pid, double cpuSeconds, uint faults = 0, string image = "x.exe") =>
        new() { Pid = pid, CreateTime = 5000 + pid, ImageName = image, KernelTime = (long)(cpuSeconds * PerSecond), PageFaultCount = faults };

    /// <summary>Feeds one sample a minute for <paramref name="minutes"/> minutes; each process runs at its cores rate.</summary>
    private static void Run(ProcessCostHistory h, int minutes, (int pid, double cores, uint faultsPerSec, string image)[] procs, Func<int, int>? foreground = null, int startMinute = 0)
    {
        for (int m = startMinute; m <= startMinute + minutes; m++)
        {
            var t = T0.AddMinutes(m);
            var samples = procs.Select(p => P(p.pid, p.cores * 60 * m, (uint)(p.faultsPerSec * 60 * m), p.image)).ToList();
            h.Observe(t, samples, foreground?.Invoke(m) ?? 0);
        }
    }

    [Fact]
    public void AveragesABusyProcessAndIgnoresAnIdleOne()
    {
        var h = new ProcessCostHistory();
        Run(h, 15, [(100, 1.7, 887_000, "esrv_svc.exe"), (200, 0.0, 0, "idle.exe")]);
        var avg = h.Average(new ProcessKey(100, 5100), TimeSpan.FromMinutes(10), T0.AddMinutes(15));
        Assert.NotNull(avg);
        Assert.Equal(1.7, avg!.Value.cores, 2);
        Assert.InRange(avg.Value.faultsPerSec, 886_900, 887_100);
        Assert.Equal(TimeSpan.FromMinutes(10), avg.Value.covered);
        Assert.Null(h.Average(new ProcessKey(200, 5200), TimeSpan.FromMinutes(10), T0.AddMinutes(15))); // never busy: not tracked
        Assert.Equal(1, h.Tracked);
    }

    [Fact]
    public void HeavyBackgroundAppIsJudgedOverThirtyMinutesNotOneWindow()
    {
        var h = new ProcessCostHistory();
        var procs = new (int, double, uint, string)[]
        {
            (100, 1.0, 0, "TrayHelper.exe"),           // about 1 core all morning, never in front
            (200, 0.4, 0, "light.exe"),                // under 0.5 core
            (300, 2.0, 0, "editor.exe"),               // busy, but its window was in front once
        };
        Run(h, 20, procs, m => m == 12 ? 300 : 0);
        Assert.Empty(h.Heavy(T0.AddMinutes(20), TimeSpan.FromMinutes(30), 0.5)); // 20 min is not 30
        Run(h, 12, procs, m => 0, startMinute: 21);
        var heavy = h.Heavy(T0.AddMinutes(33), TimeSpan.FromMinutes(30), 0.5);
        var only = Assert.Single(heavy);
        Assert.Equal(100, only.Key.Pid);
        Assert.Equal(1.0, only.Cores, 2);
        Assert.True(only.Covered >= TimeSpan.FromMinutes(30));
        // The editor's foreground minute (12) is now older than 30 min: it is judged on the last 30 minutes only.
        Run(h, 10, procs, m => 0, startMinute: 34);
        Assert.Contains(h.Heavy(T0.AddMinutes(44), TimeSpan.FromMinutes(30), 0.5), x => x.Key.Pid == 300);
    }

    [Fact]
    public void ABurstInOneWindowIsNotHeavy()
    {
        // 30 minutes mostly idle with one 10 s burst at 4 cores: one window looked heavy; the 30-min average is not.
        var h = new ProcessCostHistory();
        double cpu = 0;
        for (int m = 0; m <= 31; m++)
        {
            if (m == 5) cpu += 40; // 40 CPU-seconds in that minute
            else cpu += 4;          // ~0.07 core otherwise (stays tracked)
            h.Observe(T0.AddMinutes(m), [P(100, cpu)], 0);
        }
        Assert.Empty(h.Heavy(T0.AddMinutes(31), TimeSpan.FromMinutes(30), 0.5));
    }

    [Fact]
    public void ExitedProcessesAreForgotten()
    {
        var h = new ProcessCostHistory();
        Run(h, 3, [(100, 1.0, 0, "a.exe")]);
        Assert.Equal(1, h.Tracked);
        h.Observe(T0.AddMinutes(5), [], 0);
        Assert.Equal(0, h.Tracked);
    }
}
