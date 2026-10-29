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
        string path = Path.Combine(CiFolder(), name);
        Assert.True(File.Exists(path), $"{path} is missing");
        return Settings.Load(path, explicitConfig: true);
    }

    /// <summary>The checkout's ci folder: the test runs from bin/&lt;config&gt;/&lt;tfm&gt;/ inside it, under the folder holding PoolWatch.slnx.</summary>
    private static string CiFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return Path.Combine(dir.FullName, "ci");
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheUiTestRunsWithTheM2ThresholdsAndTheHourlyClear()
    {
        // ci/m4-ui.ps1: the Simple view's line shows a schedule and Active changes lists a setting; the rest is as in M2.
        var ui = Config("ui-config.json");
        Assert.True(ui.AutoClearEnabled);
        Assert.Equal(1, ui.AutoClearEveryHours);
        var m2 = Config("m2-config.json");
        m2.AutoClearEnabled = true;
        m2.AutoClearEveryHours = 1;
        Assert.Equal(m2.ToJson(), ui.ToJson());
    }

    [Fact]
    public void NoCiRunAsksGitHubForANewVersion()
    {
        // The update check is on by default; from shared runner addresses it would hit GitHub's rate limit, and in the soak
        // it would be a cost a user's PC has once a day, not once a run.
        var configs = Directory.GetFiles(CiFolder(), "*-config.json");
        Assert.True(configs.Length >= 5, string.Join(", ", configs));
        Assert.All(configs, path => Assert.False(Settings.Load(path, explicitConfig: true).CheckForUpdates, path));
        Assert.True(new Settings().CheckForUpdates); // the control: on by default
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
