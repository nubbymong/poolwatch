using PoolWatch.Core;
using PoolWatch.Core.Incidents;

namespace PoolWatch.Tests;

/// <summary>
/// The settings files the Windows CI scenarios run with (<c>ci/*.json</c>, read with <c>--config</c>). Several traces are
/// off by default (the on-alert trace, its Kernel-File part, the CPU trace), so a scenario that asserts on one of them must
/// turn it on in its config. These tests load the real files the way <c>--config</c> does and ask the capture governor what
/// each scenario's trace will record, so a missing setting fails here in seconds instead of in a long end-to-end run.
/// </summary>
public sealed class CiConfigTests
{
    private static Settings Config(string name)
    {
        // The test runs from bin/<config>/<tfm>/ inside the checkout; the repo root is the folder holding PoolWatch.slnx.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx")))
            {
                string path = Path.Combine(dir.FullName, "ci", name);
                Assert.True(File.Exists(path), $"{path} is missing");
                return Settings.Load(path, explicitConfig: true);
            }
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheCaptureScenarioTracesPoolEventsAndKernelFileOnAnAlert()
    {
        // ci/m2-e2e.ps1 (capture) asserts both the pool events and Leaker.exe's file opens from the on-alert trace.
        var s = Config("m3-config.json");
        Assert.True(s.CaptureOnAlert);
        var scope = CaptureGovernor.ScopeFor(CaptureSource.PoolAlert, s);
        Assert.True(scope.PoolEvents);
        Assert.True(scope.KernelFile);
    }

    [Fact]
    public void TheDiagnosticsScenariosRecordTheCpuTrace()
    {
        // ci/diag-e2e.ps1: "lag" asserts the lag trip's CPU trace, "clearcache" the CPU part of Capture now.
        var s = Config("diag-config.json");
        Assert.True(CaptureGovernor.ScopeFor(CaptureSource.LagTrip, s).Cpu);
        Assert.True(CaptureGovernor.ScopeFor(CaptureSource.Manual, s).Cpu);
    }
}
