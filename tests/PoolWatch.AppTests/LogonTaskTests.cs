using PoolWatch.Core.Findings;
using PoolWatch.Engine;
using static PoolWatch.Engine.LogonTaskPolicy;

namespace PoolWatch.AppTests;

/// <summary>
/// S4 (review round 3): the logon task starts PoolWatch with highest privileges and no UAC prompt, so it may only start an
/// exe in an admin-only install folder. Pure decisions plus the check with a fake Task Scheduler: nothing is registered.
/// </summary>
public class LogonTaskTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];
    private const string Installed = @"C:\Program Files\PoolWatch\PoolWatch.exe";
    private const string Desktop = @"C:\Users\alice\OneDrive\Desktop\PoolWatch.exe";

    [Fact]
    public void OnlyAnInstalledExeIsEverRegistered()
    {
        Assert.Null(WhyNotRegister(Installed, allowAnyLocation: false, uiSmoke: false, Roots));
        Assert.Contains("you can write to", WhyNotRegister(Desktop, false, false, Roots));
        Assert.Contains("--allow-any-location", WhyNotRegister(Installed, allowAnyLocation: true, uiSmoke: false, Roots));
        Assert.Contains("UI test", WhyNotRegister(Installed, false, uiSmoke: true, Roots));
        Assert.NotNull(WhyNotRegister(null, false, false, Roots));
        Assert.NotNull(WhyNotRegister(@"C:\Windows\Temp\PoolWatch.exe", false, false, Roots));
    }

    [Fact]
    public void ADesktopLogonTaskIsUntrustedAndCanBeRepointed()
    {
        var v = Judge(new ExistingTask(Desktop, "--tray", Highest: true), Installed, currentExeTrusted: true, Roots, p => p);
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.Equal(Desktop, v.Program);
        Assert.Contains(Desktop, v.Message);
        Assert.Contains("highest privileges", v.Message);
        Assert.True(v.CanRePoint);
        // A PoolWatch that is not itself installed may not take its place.
        Assert.False(Judge(new ExistingTask(Desktop, "--tray", true), Desktop, currentExeTrusted: false, Roots, p => p).CanRePoint);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\PoolWatch\PoolWatch.exe""", true)]           // quoted
    [InlineData(@"C:\Program Files (x86)\PoolWatch\PoolWatch.exe", true)]
    [InlineData(@"%ProgramFiles%\PoolWatch\PoolWatch.exe", true)]                   // expanded first
    [InlineData(@"%USERPROFILE%\Desktop\PoolWatch.exe", false)]
    [InlineData(@"C:\Windows\Temp\PoolWatch.exe", false)]                       // %WINDIR% is not admin-only
    [InlineData(@"C:\Program Files\..\Users\x\PoolWatch.exe", false)]
    [InlineData(@"PoolWatch.exe", false)]                                       // relative: anywhere
    [InlineData("", false)]
    public void TheTasksProgramIsJudgedByWhereItReallyIs(string command, bool fine)
    {
        string Expand(string p) => p.Replace("%ProgramFiles%", @"C:\Program Files").Replace("%USERPROFILE%", @"C:\Users\x");
        Assert.Equal(fine ? Judgement.Fine : Judgement.Untrusted, Judge(new ExistingTask(command, "--tray", true), Installed, true, Roots, Expand).Judgement);
    }

    [Fact]
    public void NoTaskNothingToDo() => Assert.Equal(Judgement.NoTask, Judge(null, Installed, true, Roots).Judgement);

    [Fact]
    public void EveryActionIsJudgedNotOnlyTheFirst()
    {
        // L2: Task Scheduler runs every action with the task's privileges. A safe first action used to hide an unsafe second one.
        var twoActions = new ExistingTask([new TaskAction(Installed, "--tray"), new TaskAction(Desktop, "--tray")], Highest: true);
        var v = Judge(twoActions, Installed, currentExeTrusted: true, Roots, p => p);
        Assert.Equal(Judgement.Untrusted, v.Judgement);
        Assert.Equal(Desktop, v.Program);
        Assert.True(v.CanRePoint); // re-pointing replaces the whole task with the one installed exe

        // An action that is not a program (a COM handler can load code the user registered) cannot be checked: untrusted.
        var com = new ExistingTask([new TaskAction(Installed, "--tray"), new TaskAction("a task action of type 5 (not a program)", null, IsExec: false)], true);
        var c = Judge(com, Installed, true, Roots, p => p);
        Assert.Equal(Judgement.Untrusted, c.Judgement);
        Assert.Contains("cannot check", c.Message);

        // No action at all: nothing safe to point at, untrusted (as before).
        Assert.Equal(Judgement.Untrusted, Judge(new ExistingTask([], true), Installed, true, Roots, p => p).Judgement);

        // Every action safe: fine.
        var bothSafe = new ExistingTask([new TaskAction(Installed, "--tray"), new TaskAction(@"C:\Program Files (x86)\Tool\tool.exe", null)], true);
        Assert.Equal(Judgement.Fine, Judge(bothSafe, Installed, true, Roots, p => p).Judgement);
    }

    // ------------------------------------------------------------------ the check, with a fake Task Scheduler

    private sealed class FakeTasks(ExistingTask? task) : ILogonTasks
    {
        public readonly List<string> Calls = new();
        public ExistingTask? Read() => task;
        public string? RegisterCurrent() { Calls.Add("register"); return null; }
        public string? Remove() { Calls.Add("remove"); return null; }
    }

    private static (List<string> calls, List<Finding> findings, int asked) Check(ExistingTask? task, LogonTaskCheck.Answer answer, bool currentTrusted = true)
    {
        var tasks = new FakeTasks(task);
        var findings = new List<Finding>();
        int asked = 0;
        new LogonTaskCheck(tasks, _ => { asked++; return answer; }, _ => { }, findings.Add).Run(Installed, currentTrusted, Roots);
        return (tasks.Calls, findings, asked);
    }

    [Fact]
    public void TheUserChoosesRepointRemoveOrLeave()
    {
        var desktop = new ExistingTask(Desktop, "--tray", true);
        var repointed = Check(desktop, LogonTaskCheck.Answer.RePoint);
        Assert.Equal(["register"], repointed.calls);
        Assert.Contains("pointed it at " + Installed, Assert.Single(repointed.findings).Title);

        var removed = Check(desktop, LogonTaskCheck.Answer.Remove);
        Assert.Equal(["remove"], removed.calls);

        var left = Check(desktop, LogonTaskCheck.Answer.Leave);
        Assert.Empty(left.calls);
        var f = Assert.Single(left.findings);
        Assert.Equal(FindingKind.Setup, f.Kind);
        Assert.Equal(FindingSeverity.Warning, f.Severity);
        Assert.Contains("asks again", f.Title);
    }

    [Fact]
    public void NothingIsAskedOrChangedForASafeTaskAndAnUninstalledPoolWatchNeverRepoints()
    {
        var safe = Check(new ExistingTask(Installed, "--tray", true), LogonTaskCheck.Answer.RePoint);
        Assert.Equal(0, safe.asked);
        Assert.Empty(safe.calls);
        Assert.Empty(safe.findings);

        var notInstalled = Check(new ExistingTask(Desktop, "--tray", true), LogonTaskCheck.Answer.RePoint, currentTrusted: false);
        Assert.Empty(notInstalled.calls); // re-pointing to an untrusted exe is never done, whatever the answer
    }
}
