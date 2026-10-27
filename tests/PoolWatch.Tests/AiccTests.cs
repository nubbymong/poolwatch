using PoolWatch.Core;
using PoolWatch.Core.Aicc;
using PoolWatch.Core.Model;

namespace PoolWatch.Tests;

public class AiccTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 6, 0, 0, DateTimeKind.Utc);
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude");

    [Theory]
    [InlineData(@"""C:\Program Files\Git\usr\bin\find.exe"" / -maxdepth 6 -type d -name release-notes-*", "/ -maxdepth 6 -type d -name")]
    [InlineData(@"C:\Git\bin\bash.exe -c ""find / -maxdepth 6 -type d -name x""", "find / -maxdepth 6 -type d")]
    [InlineData(@"C:\Git\bin\bash.exe -c -l ""source /tmp/snap.sh && eval 'find . -name *.md -type f -mtime -1' < /dev/null""", "find . -name *.md -type f")]
    [InlineData(@"C:\Windows\system32\cmd.exe /d /s /c ""npm run build --workspace web""", "npm run build --workspace web")]
    [InlineData(@"cmd /C ""npm run build""", "npm run build")]
    [InlineData(@"pwsh.exe -NoProfile -Command ""Get-ChildItem -Recurse -Filter *.log""", "Get-ChildItem -Recurse -Filter *.log")]
    [InlineData(@"git.exe -c core.quotepath=false status --porcelain", "-c core.quotepath=false status --porcelain")]
    [InlineData(@"cmd.exe /d /s /c", null)]
    [InlineData(@"notepad.exe", null)]
    public void CommandCoreIsJsonSafeAndDistinctive(string commandLine, string? expected) =>
        Assert.Equal(expected, TranscriptSearch.CommandCore(commandLine) is { } c ? string.Join(' ', c) : null);

    private static void Touch() { foreach (var f in Directory.EnumerateFiles(Fixtures, "*.jsonl", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, T0.AddMinutes(9)); }

    [Fact]
    public void FindsTheSubagentToolCallThatIssuedTheCommand()
    {
        Touch();
        // As MSYS find.exe sees it: bash already stripped the quotes the transcript still has (\"release-notes-*\").
        var hit = TranscriptSearch.Find([Fixtures], @"C:\Git\usr\bin\find.exe / -maxdepth 6 -type d -name release-notes-*", T0.AddMinutes(8).AddSeconds(33));
        Assert.NotNull(hit);
        Assert.Equal("00000000-0000-4000-8000-000000000001", hit!.SessionId);
        Assert.Equal("D--sample-app", hit.Project);
        Assert.Equal("wf_00000001", hit.WorkflowId);
        Assert.Equal("agent-0001", hit.SubagentId);
        Assert.Contains("-maxdepth 6 -type d -name", hit.Command);
        Assert.Equal(T0.AddMinutes(8).AddSeconds(31), hit.TimestampUtc);
    }

    [Fact]
    public void PicksTheCallJustBeforeTheStartAndIgnoresLaterOnes()
    {
        Touch();
        // The fixture has the same command at 06:02 (session 2) and 06:08:31 (sub-agent); a process started at 06:03 is the first one.
        var early = TranscriptSearch.Find([Fixtures], "find.exe / -maxdepth 6 -type d -name release-notes-*", T0.AddMinutes(3));
        Assert.Equal("b0000000-0000-0000-0000-000000000002", early!.SessionId);
        Assert.Equal(T0.AddMinutes(2), early.TimestampUtc);
    }

    [Fact]
    public void IgnoresTranscriptsOutsideTheWindowNonToolTextAndRespectsBudgets()
    {
        Touch();
        Assert.Null(TranscriptSearch.Find([Fixtures], "find.exe / -maxdepth 6 -type d -name x", T0.AddHours(3))); // modified long before the start
        Assert.Null(TranscriptSearch.Find([Fixtures], "find.exe /q -maxdepth 7 -type f -iname y", T0.AddMinutes(8))); // no tool call has it
        Assert.Null(TranscriptSearch.Find([Fixtures], "find.exe / -maxdepth 6 -type d -name x", T0.AddMinutes(8).AddSeconds(33), budget: new SearchBudget(MaxFiles: 0)));
        Assert.Null(TranscriptSearch.Find([Fixtures], "find.exe / -maxdepth 6 -type d -name x", T0.AddMinutes(8).AddSeconds(33), budget: new SearchBudget(MaxBytes: 0)));
    }

    [Fact]
    public void ProfileFromUserProfileAndWorktreeDeletes()
    {
        var ctx = SessionContext.From(new Dictionary<string, string> { [SessionContext.UserProfileKey] = @"D:\R\account-profiles\profile-ab12" })!;
        Assert.Equal("profile-ab12", ctx.Profile);
        Assert.Equal(Path.Combine(@"D:\R\account-profiles\profile-ab12", ".claude"), ctx.ConfigDir);
        Assert.True(PoolWatch.Core.Incidents.IncidentManager.IsWorktreeDelete("git worktree remove --force ../wt-7"));
        Assert.True(PoolWatch.Core.Incidents.IncidentManager.IsWorktreeDelete(@"rm -rf D:/sample-app/.claude/worktrees/agent-3"));
        Assert.False(PoolWatch.Core.Incidents.IncidentManager.IsWorktreeDelete(@"node build.js --out D:\sample-app\worktrees\a"));
        Assert.False(PoolWatch.Core.Incidents.IncidentManager.IsWorktreeDelete(@"docker run --rm -v D:/x/.git/worktrees:/w img"));
        Assert.True(PoolWatch.Core.Incidents.IncidentManager.IsWorktreeDelete(@"cmd /c rd /s /q D:\sample-app\.git\worktrees\wt-2"));
    }

    [Fact]
    public void TheAiccRootComesFromTheSettingOrFromTheSessionItself()
    {
        // The setting is empty by default (no machine-specific folder is built in): the session's own config dir names it.
        Assert.Equal([@"D:\aicc"], SessionContext.AiccRoots("", @"D:\aicc\account-profiles\profile-7c1d\.claude"));
        Assert.Equal([@"E:\res", @"D:\aicc"], SessionContext.AiccRoots(@"E:\res\", @"D:\aicc\account-profiles\profile-7c1d"));
        Assert.Equal([@"D:\aicc"], SessionContext.AiccRoots(@"D:\aicc", @"d:\AICC\account-profiles\profile-1\.claude")); // once
        Assert.Empty(SessionContext.AiccRoots("", null));
        Assert.Empty(SessionContext.AiccRoots("", @"C:\Users\alice\.claude"));              // not an AICC profile
        Assert.Empty(SessionContext.AiccRoots(@"\\server\share\aicc", @"\\server\share\aicc\account-profiles\profile-1\.claude")); // never a share
        Assert.Empty(SessionContext.AiccRoots("relative", null));
        Assert.Equal([@"D:\"], SessionContext.AiccRoots(@"D:\", null)); // a drive root stays a root, never the drive-relative "D:"
    }

    private static ProcessSample P(int pid, int ppid, int min, string name) => new() { Pid = pid, ParentPid = ppid, CreateTime = T0.AddMinutes(min).ToFileTimeUtc(), ImageName = name };

    [Fact]
    public void LabelsAiccTreeAndElectronRoles()
    {
        var r = new ProcessRegistry
        {
            Enricher = rec => rec.CommandLine = rec.Pid switch
            {
                11 or 12 => "\"AI Code Conductor.exe\" --type=utility --utility-sub-type=node.mojom.NodeService",
                13 => "\"AI Code Conductor.exe\" --type=renderer",
                31 => "ChatGPT.exe --type=gpu-process",
                _ => rec.Image,
            },
        };
        r.ObserveSnapshot([P(4, 0, 0, "System"), P(10, 4, 1, "AI Code Conductor.exe"), P(11, 10, 2, "AI Code Conductor.exe"), P(12, 10, 2, "AI Code Conductor.exe"),
            P(13, 10, 2, "AI Code Conductor.exe"), P(20, 10, 3, "conhost.exe"), P(21, 20, 3, "claude.exe"), P(22, 21, 4, "bash.exe"), P(23, 22, 5, "find.exe"),
            P(30, 4, 1, "ChatGPT.exe"), P(31, 30, 1, "ChatGPT.exe")], T0.AddMinutes(6));
        ProcessLabels.Apply(r, new Dictionary<ProcessKey, long> { [new(11, T0.AddMinutes(2).ToFileTimeUtc())] = 900_000, [new(12, T0.AddMinutes(2).ToFileTimeUtc())] = 1000 });
        string? L(int pid) => r.GetLive(pid)!.Label;
        Assert.Equal("AICC main", L(10));
        Assert.Equal("AICC Tokenomics worker", L(11));
        Assert.Equal("AICC worker", L(12));
        Assert.Equal("AICC renderer", L(13));
        Assert.Equal("AICC PTY host", L(20));
        Assert.Equal("AICC session", L(21));
        Assert.Equal("AICC session", L(22));
        Assert.Equal("under Claude Code session (PID 21)", L(23)); // a shared tool is named by whom it works for
        Assert.Equal("ChatGPT gpu-process", L(31));

        r.GetLive(21)!.Context[SessionContext.ConfigDirKey] = @"D:\aicc\account-profiles\profile-7c1d\.claude";
        r.GetLive(22)!.Context[SessionContext.CwdKey] = @"D:\sample-app\";
        var chain = r.Ancestry(new ProcessKey(23, T0.AddMinutes(5).ToFileTimeUtc()));
        var ctx = SessionContext.ForChain(chain);
        Assert.Equal("profile-7c1d", SessionContext.From(r.GetLive(21)!.Context)!.Profile);
        Assert.Equal(@"D:\sample-app\", ctx!.Cwd);
        // bash's cwd (nearest) and the claude.exe ancestor's profile are merged.
        Assert.Equal("profile-7c1d", ctx.Profile);
        Assert.Equal(@"AICC › D:\sample-app (profile-7c1d) › claude › bash › find.exe", SessionContext.Describe(chain, ctx));
    }

    [Fact]
    public void ParsesEnvironmentBlockWithDriveEntriesAndTrailingNulls()
    {
        string block = "=C:=C:\\work\0=::=::\\\0USERPROFILE=C:\\Users\\alice\0CLAUDE_CONFIG_DIR=D:\\R\\account-profiles\\profile-7c1d\\.claude\0PATH=x\0\0\0";
        var env = EnvironmentBlock.Parse(block, ["USERPROFILE", "claude_config_dir"]);
        Assert.Equal(2, env.Count);
        Assert.Equal(@"C:\Users\alice", env["USERPROFILE"]);
        Assert.Equal("profile-7c1d", SessionContext.From(new Dictionary<string, string> { [SessionContext.ConfigDirKey] = env["CLAUDE_CONFIG_DIR"] })!.Profile);
        Assert.Empty(EnvironmentBlock.Parse("", ["PATH"]));
    }

    [Fact]
    public void ReportNamesTheClaudeCodeSession()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-aicc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5 };
            var w = new World();
            var host = new FakeHost(dir)
            {
                Session = new PoolWatch.Core.Incidents.SessionDetails(@"AICC › D:\sample-app (profile-7c1d) › claude › bash › find.exe",
                    new SessionContext(@"D:\sample-app", "profile-7c1d", @"D:\R\profile-7c1d\.claude"),
                    new TranscriptHit("00000000-0000-4000-8000-000000000001", "D--sample-app", "wf_00000001", "agent-0001", World.T0, "agent-0001.jsonl", "find / -maxdepth 6"), false),
            };
            var mgr = new PoolWatch.Core.Incidents.IncidentManager(settings, new PoolWatch.Core.Tags.TagKnowledge(null, null), host, w.Registry);
            var det = new PoolWatch.Core.Detection.Detector(settings);
            w.AddProcess(4, 0, "System");
            w.AddProcess(60, 4, "find.exe");
            long tag = 20 * Settings.MB;
            var rnd = new Random(5);
            for (int i = 0; i < 10; i++) { w.Sample(("RefF", tag)); mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval); }
            for (int i = 0; i < 20; i++) { int ops = 2000 + rnd.Next(2000); w.Io(60, other: ops); tag += ops * 300; w.Sample(("RefF", tag)); mgr.Process(w.History, det, det.Evaluate(w.History), w.Now - w.Interval); }
            mgr.Finish(w.History, w.Now);
            var inc = mgr.All.Single(i => i.Tag == PoolTag.Parse("RefF"));
            var md = File.ReadAllText(Path.Combine(inc.Dir, "report.md"));
            // M1: the report as written says it is not redacted, in report.md and in report.json.
            Assert.Contains(PoolWatch.Core.Incidents.Exporter.UnredactedNotice, md);
            Assert.Contains(PoolWatch.Core.Incidents.Exporter.UnredactedNotice, File.ReadAllText(Path.Combine(inc.Dir, "report.json")));
            Assert.Contains("by Claude Code session 00000000 / workflow wf_00000001", md);
            Assert.Contains("- AICC / Claude Code: AICC › D:\\sample-app (profile-7c1d) › claude › bash › find.exe", md);
            Assert.Contains("Issued by Claude Code session `00000000-0000-4000-8000-000000000001` in project `D--sample-app`, workflow `wf_00000001`, sub-agent `agent-0001`", md);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
