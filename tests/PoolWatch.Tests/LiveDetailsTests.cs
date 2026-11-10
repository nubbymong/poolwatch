using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolWatch.Core;
using PoolWatch.Core.Aicc;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Report;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Util;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>
/// An open incident's details and Copy summary come from the incident in memory (<see cref="IncidentManager.SummaryJson"/>),
/// so they are never behind its folder, which the write budget can leave up to about an hour behind on a busy PC; and its
/// export writes the folder whole first (<see cref="IncidentManager.WriteNow"/>), so the zip holds its state now.
/// </summary>
public sealed class LiveDetailsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dir)) File.Delete(_dir);
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A world with a growth incident open, and how it goes on: <see cref="Step"/> takes one more sample.</summary>
    private sealed class Run(IncidentManager mgr, World w, Detector det, FakeHost host)
    {
        public IncidentManager Mgr { get; } = mgr;
        public World W { get; } = w;
        public FakeHost Host { get; } = host;
        public long Tag = 100L << 20;
        public int Pid = 700_000;

        /// <summary>One sample: node.exe does its I/O, the tag grows by <paramref name="grow"/>, a git.exe starts.</summary>
        public void Step(long grow)
        {
            W.Io(100, other: 400);
            Tag += grow;
            W.Sample(("Leak", Tag));
            var now = W.Now - W.Interval;
            Mgr.AddEvents([new LifecycleEvent { TimeUtc = now, Kind = LifecycleKind.Start, Pid = Pid++, CreateTime = now.ToFileTimeUtc(), ParentPid = 100, Image = "git.exe", CommandLine = "git status" }], At(now));
            Mgr.Process(W.History, det, det.Evaluate(W.History), At(now));
        }
    }

    /// <summary>
    /// A growth incident opens (its report written at once), then the write budget has no room for an hour while the tag
    /// goes on growing and a node.exe does the I/O: report.json still says what it said an hour ago.
    /// </summary>
    private (IncidentManager Mgr, World W, Incident Inc, string WrittenJson, int Pid) AnHourBehind()
    {
        var (run, inc, written) = AnHourBehindRun();
        return (run.Mgr, run.W, inc, written, run.Pid);
    }

    private (Run Run, Incident Inc, string WrittenJson) AnHourBehindRun()
    {
        Directory.CreateDirectory(_dir);
        var w = new World { Interval = TimeSpan.FromSeconds(10) };
        var settings = new Settings { GrowthRules = [new(10, 50 * Settings.MB)] };
        var host = new FakeHost(_dir);
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), host, w.Registry) { RewriteInterval = TimeSpan.Zero };
        var run = new Run(mgr, w, new Detector(settings), host);
        w.AddProcess(4, 0, "System");
        w.AddProcess(100, 4, "node.exe");
        Incident? inc = null;
        string? written = null;
        for (int i = 0; i < 30 + 6 * 70; i++)
        {
            run.Step(i >= 30 ? 1L << 20 : 0); // 6 MB a minute from minute 5
            if (inc is null && mgr.Open.Count > 0)
            {
                inc = mgr.Open.Single();
                written = File.ReadAllText(Path.Combine(inc.Dir, "report.json"));
                mgr.Budget = new WriteBudget(bytesPerHour: 0, burst: 1); // no room from now on
                mgr.Budget.Spend(1, At(w.Now - w.Interval).Ms);
            }
        }
        Assert.NotNull(inc);
        Assert.True(inc!.IsOpen);
        Assert.Equal(written, File.ReadAllText(Path.Combine(inc.Dir, "report.json"))); // its folder is an hour behind
        return (run, inc, written!);
    }

    [Fact]
    public void AnOpenIncidentWhoseReportIsAnHourOldShowsWhatPoolWatchKnowsNow()
    {
        var (mgr, w, inc, written, _) = AnHourBehind();
        string live = mgr.SummaryJson(w.History, inc);
        string shown = CopyText.IncidentDetails(live), stale = CopyText.IncidentDetails(written);
        Assert.NotEqual(stale, shown);
        Assert.Contains($"grew {Fmt.Bytes(inc.Growth)}", shown);
        Assert.DoesNotContain($"grew {Fmt.Bytes(inc.Growth)}", stale);
        Assert.Contains("node.exe", shown); // its suspects as ranked now
        Assert.Contains($"grew {Fmt.Bytes(inc.Growth)}", CopyText.IncidentSummary(live));
        // What the folder says once written now is what the details showed: the same summary, from the same builder.
        Assert.True(mgr.WriteNow(w.History, inc));
        string file = File.ReadAllText(Path.Combine(inc.Dir, "report.json"));
        Assert.Equal(CopyText.IncidentSummary(file), CopyText.IncidentSummary(live));
        Assert.Equal(CopyText.IncidentDetails(file), shown);
    }

    /// <summary>
    /// Report an issue's digest of an open incident is built from the same summary (ReportIssueDialog.Digest reads
    /// UiData.Live first): it says what PoolWatch knows now, as its report.json says it once written, not what the hour-old
    /// file said; the suspect is named as the file names it (its image is in the summary).
    /// </summary>
    [Fact]
    public void ReportAnIssuesDigestOfAnOpenIncidentIsTheLiveOne()
    {
        var (mgr, w, inc, written, _) = AnHourBehind();
        string? live = IssueReport.IncidentDigest(mgr.SummaryJson(w.History, inc)), stale = IssueReport.IncidentDigest(written);
        Assert.NotNull(live);
        Assert.NotEqual(stale, live);
        Assert.Contains($"grew {Fmt.Bytes(inc.Growth)}", live);
        Assert.Contains("node.exe", live);
        Assert.True(mgr.WriteNow(w.History, inc));
        Assert.Equal(IssueReport.IncidentDigest(File.ReadAllText(Path.Combine(inc.Dir, "report.json"))), live);
    }

    /// <summary>
    /// Every field the live summary has is report.json's, written at the same moment (the summary is not a second copy of
    /// the report that could drift from it): with the top suspect's Claude Code session in the headline, then with a trace.
    /// </summary>
    [Fact]
    public void EachFieldOfTheLiveSummaryIsReportJsonsWrittenAtTheSameMoment()
    {
        var (run, inc, _) = AnHourBehindRun();
        void Same(string why)
        {
            string live = run.Mgr.SummaryJson(run.W.History, inc);
            Assert.True(run.Mgr.WriteNow(run.W.History, inc));
            var summary = JsonNode.Parse(live)!.AsObject();
            var report = JsonNode.Parse(File.ReadAllText(Path.Combine(inc.Dir, "report.json")))!.AsObject();
            Assert.True(summary.Count >= 7, why);
            foreach (var (name, value) in summary)
            {
                if (name != "suspects")
                {
                    Assert.True(JsonNode.DeepEquals(value, report[name]), $"{why}: {name} is {value?.ToJsonString()} live, {report[name]?.ToJsonString()} in report.json");
                    continue;
                }
                var mine = value!.AsArray();
                var theirs = report["suspects"]!.AsArray();
                Assert.NotEmpty(mine);
                for (int i = 0; i < mine.Count; i++)
                    foreach (var (field, v) in mine[i]!.AsObject())
                        Assert.True(JsonNode.DeepEquals(v, theirs[i]![field]), $"{why}: suspects[{i}].{field} is {v?.ToJsonString()} live, {theirs[i]![field]?.ToJsonString()} in report.json");
            }
        }
        run.Host.Session = new SessionDetails("node.exe < claude.exe", new SessionContext(@"C:\work\app", null, null),
            new TranscriptHit("5f1e9c2a-0b7d-4c1e-9a3f-2d6b8e4c1a77", "app", "wf-42", null, null, @"C:\t\5f1e9c2a.jsonl", "npm test"), false);
        Same("with a session");
        Assert.Contains("by Claude Code session 5f1e9c2a / workflow wf-42", JsonNode.Parse(run.Mgr.SummaryJson(run.W.History, inc))!["headline"]!.GetValue<string>());
        inc.TraceOwners = [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:node", "node.exe") }];
        Same("with a trace");
        Assert.Contains("the confirmation trace shows node.exe allocating", JsonNode.Parse(run.Mgr.SummaryJson(run.W.History, inc))!["headline"]!.GetValue<string>());
    }

    /// <summary>
    /// What the budget held back is counted where About and profile.json read it: the writes held for a later sample, the
    /// open incident whose report is behind, with how long ago it was last written, and its chart and CSV files, not written
    /// since it opened; none once it is written.
    /// </summary>
    [Fact]
    public void AnOpenIncidentsFolderBehindAndTheWritesHeldBackAreCounted()
    {
        var (mgr, w, inc, _, _) = AnHourBehind();
        Assert.True(mgr.Budget.Held > 0);
        var behind = mgr.BehindNow(w.History);
        Assert.Equal(1, behind.Reports);
        Assert.InRange(behind.OldestReport!.Value.TotalMinutes, 55, 75);
        Assert.Equal(1, behind.Charts); // written with its opening (the budget had room to spare then), not since
        Assert.InRange(behind.OldestChart!.Value.TotalMinutes, 55, 75);
        Assert.Equal(0, behind.Failed);
        Assert.True(mgr.WriteNow(w.History, inc));
        Assert.Equal(FoldersBehind.None, mgr.BehindNow(w.History));
    }

    /// <summary>
    /// A write that failed (logged, not retried until the incident's next change) leaves its folder behind: counted as
    /// behind and as failed until a write is made, though the incident is no longer marked as changed.
    /// </summary>
    [Fact]
    public void AnOpenIncidentWhoseLastWriteFailedIsBehind()
    {
        var (mgr, w, inc, _, _) = AnHourBehind();
        Assert.True(mgr.WriteNow(w.History, inc));
        Assert.Equal(FoldersBehind.None, mgr.BehindNow(w.History));
        Directory.Delete(inc.Dir, recursive: true);
        File.WriteAllText(inc.Dir, "a file where its folder was"); // the next write cannot make the folder
        Assert.False(mgr.WriteNow(w.History, inc));
        Assert.False(inc.Dirty);
        var behind = mgr.BehindNow(w.History);
        Assert.Equal((1, 1), (behind.Reports, behind.Failed));
        File.Delete(inc.Dir);
        Assert.True(mgr.WriteNow(w.History, inc));
        Assert.Equal(FoldersBehind.None, mgr.BehindNow(w.History));
    }

    /// <summary>
    /// Under load the chart and CSV files wait for room to spare while the report is written: the report is current, the
    /// chart is counted as behind, from its last write.
    /// </summary>
    [Fact]
    public void AnOpenIncidentsChartWrittenHalfAnHourAgoOrMoreIsBehindWhileItsReportIsCurrent()
    {
        var (run, inc, _) = AnHourBehindRun();
        Assert.True(run.Mgr.WriteNow(run.W.History, inc));
        // Room for the reports (12 MB an hour), never room to spare: half its 64 MB burst, which is spent at once.
        run.Mgr.Budget = new WriteBudget(bytesPerHour: 12L << 20, burst: 64L << 20);
        run.Mgr.Budget.Spend(64L << 20, run.W.History.Ms[^1], must: true);
        for (int i = 0; i < 6 * 35; i++) run.Step(1L << 20);
        var behind = run.Mgr.BehindNow(run.W.History);
        Assert.Equal(0, behind.Reports);
        Assert.Equal(1, behind.Charts);
        Assert.InRange(behind.OldestChart!.Value.TotalMinutes, 30, 36);
    }

    /// <summary>
    /// The window's details read an open incident's summary through <see cref="IncidentManager.Live"/>: the same instance
    /// while nothing it says can have changed (its growth alone moves at nearly every sample, and is brought up to date by
    /// the next ranking); built again after a ranking, a trace or a write; none once the incident closed (its report.json is
    /// read then).
    /// </summary>
    [Fact]
    public void TheLiveSummaryIsBuiltAgainOnlyWhenWhatItSaysCanHaveChanged()
    {
        var (run, inc, _) = AnHourBehindRun();
        var h = run.W.History;
        var first = run.Mgr.Live.Get(h, inc.Dir)!;
        int builds = run.Mgr.Live.Builds;
        Assert.Same(first, run.Mgr.Live.Get(h, inc.Dir.ToUpperInvariant()));
        // The growth moved, nothing else: the same summary until the next ranking.
        long growth = inc.Growth;
        inc.PeakBytes += 5L << 20;
        Assert.NotEqual(growth, inc.Growth);
        Assert.Same(first, run.Mgr.Live.Get(h, inc.Dir));
        // A ranking alone (every sample while it waits for its write here, RewriteInterval 0; a sample with no growth, so no
        // alert): built again.
        var (rankedAt, alerts, suspects, writtenAt) = (inc.LastRankMs, inc.Alerts.Count, inc.Suspects.Count, inc.LastWriteMs);
        run.Step(0);
        Assert.NotEqual(rankedAt, inc.LastRankMs);
        Assert.Equal((alerts, suspects, writtenAt), (inc.Alerts.Count, inc.Suspects.Count, inc.LastWriteMs));
        var ranked = run.Mgr.Live.Get(h, inc.Dir)!;
        Assert.NotSame(first, ranked);
        Assert.Contains($"grew {Fmt.Bytes(inc.Growth)}", ranked.Json);
        Assert.Same(ranked, run.Mgr.Live.Get(h, inc.Dir));
        // A trace: built again.
        inc.TraceOwners = [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:node", "node.exe") }];
        var traced = run.Mgr.Live.Get(h, inc.Dir)!;
        Assert.NotSame(ranked, traced);
        Assert.Contains("the confirmation trace shows node.exe", traced.Json);
        // A write: built again, with when the folder was written.
        Assert.True(run.Mgr.WriteNow(h, inc));
        var written = run.Mgr.Live.Get(h, inc.Dir)!;
        Assert.NotSame(traced, written);
        Assert.Equal(inc.LastWriteUtc, written.WrittenUtc);
        Assert.Equal(builds + 3, run.Mgr.Live.Builds);
        // Closed (quiet for its 10 minutes): no live summary; its report.json is read.
        for (int i = 0; i < 6 * 15 && inc.IsOpen; i++) run.Step(0);
        Assert.False(inc.IsOpen);
        Assert.Null(run.Mgr.Live.Get(h, inc.Dir));
    }

    [Fact]
    public void AnExportOfAnOpenIncidentHoldsItsLatestEvents()
    {
        var (mgr, w, inc, _, nextPid) = AnHourBehind();
        var before = File.ReadAllLines(Path.Combine(inc.Dir, "events.csv")).Skip(1).Select(Csv.Split).Where(c => c[1] == "Start").ToList();
        Assert.True(before.Count < inc.Events.Count(e => e.Kind == LifecycleKind.Start), "the budget held rows back");
        Assert.True(mgr.WriteNow(w.History, inc));
        string zip = Exporter.Export(inc.Dir, Path.Combine(_dir, "exports"), null, p => p);
        using var archive = ZipFile.OpenRead(zip);
        using var events = new StreamReader(archive.GetEntry("events.csv")!.Open());
        var rows = events.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => Csv.Split(l.TrimEnd('\r'))).Where(c => c[1] == "Start").ToList();
        // Every event from its first row on (each one PID more than the one before), up to the newest of its window as it is
        // now (to a minute after the growth's end), which is the newest event it kept; the rows before a later onset stay.
        var pids = rows.Select(r => int.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(Enumerable.Range(pids[0], pids.Count), pids);
        long end = IncidentManager.EventWindowEndMs(inc, w.History.Ms[^1]);
        Assert.Equal(inc.Events.Where(e => e.Kind == LifecycleKind.Start && e.Ms <= end).Max(e => e.Pid), pids[^1]);
        Assert.True(pids[^1] > before.Max(c => int.Parse(c[2], System.Globalization.CultureInfo.InvariantCulture)) + 300, "an hour of rows the budget held back");
        Assert.True(nextPid - 1 - pids[^1] < 6 * 5, "the window ends within minutes of now");
        using var report = new StreamReader(archive.GetEntry("report.json")!.Open());
        Assert.Contains($"grew {Fmt.Bytes(inc.Growth)}", JsonDocument.Parse(report.ReadToEnd()).RootElement.GetProperty("headline").GetString());
    }
}
