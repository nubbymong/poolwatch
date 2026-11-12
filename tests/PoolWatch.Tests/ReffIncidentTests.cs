using PoolWatch.Core;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;

using static PoolWatch.Tests.Clocks;

namespace PoolWatch.Tests;

/// <summary>A real Reff incident (FCB_DATA grew while an agent session ran short shell commands), reduced.</summary>
public class ReffIncidentTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 6, 34, 0, DateTimeKind.Utc);
    private static readonly PoolTag Reff = PoolTag.Parse("Reff");
    private const long MB = 1L << 20, KB = 1L << 10;

    private static long Ft(DateTime t) => t.ToFileTimeUtc();

    /// <summary>claude.exe > bash.exe per command > grep / cut / git / date ...; the trace rows of those processes.</summary>
    private static (ProcessRegistry reg, CaptureResult cap) Session(int shortCommands)
    {
        var reg = new ProcessRegistry();
        var claudeCt = Ft(T0.AddHours(-1));
        var claude = reg.OnStart(4000, 1, claudeCt, "claude.exe", @"C:\Users\alice\.local\bin\claude.exe", "claude --resume", null, T0.AddHours(-1));
        lock (claude.Context) claude.Context["cwd"] = @"D:\sample-app";
        var pool = new List<CapturePoolUsage>();
        var traceStart = T0.AddMinutes(10);
        int pid = 5000;
        void Command(string image, string cl, long net, int allocs, double atSec, double runSec)
        {
            var t = traceStart.AddSeconds(atSec);
            int bash = pid++, child = pid++;
            reg.OnStart(bash, 4000, Ft(t), "bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe", $"bash -c \"{cl}\"", null, t);
            reg.OnStart(child, bash, Ft(t.AddMilliseconds(5)), image, @"C:\Program Files\Git\usr\bin\" + image, cl, null, t);
            reg.OnStop(child, Ft(t.AddMilliseconds(5)), 0, t.AddSeconds(runSec));
            reg.OnStop(bash, Ft(t), 0, t.AddSeconds(runSec + 0.01));
            pool.Add(new CapturePoolUsage(child, image, Reff, net, 0, allocs, 0, [], Ft(t.AddMilliseconds(5)), cl, bash, t.AddMilliseconds(10)));
        }
        Command("grep.exe", "grep -rn TODO .", 82 * MB, 60_000, 1, 8);
        Command("cut.exe", "cut -d: -f1", 65 * MB, 48_000, 2, 7);
        string[] shorts = ["git.exe", "date.exe", "timeout.exe", "bash.exe"];
        for (int i = 0; i < shortCommands; i++) Command(shorts[i % shorts.Length], "git status", 700 * KB, 540, 10 + i * 0.1, 0.2);
        // A reused PID: the same number as grep, a different process later, nothing to do with the session.
        var cap = new CaptureResult { PoolEventsParsed = true, StartUtc = traceStart, EndUtc = traceStart.AddMinutes(1), StartMs = Clocks.At(traceStart).Ms, EndMs = Clocks.At(traceStart.AddMinutes(1)).Ms, Tags = [Reff], Pool = pool };
        return (reg, cap);
    }

    private static Incident Inc() => new()
    {
        Id = "x", Dir = "x", Tag = Reff, Kind = IncidentKind.Growth, OnsetUtc = T0, LastRiseUtc = T0.AddMinutes(26), EndUtc = T0.AddMinutes(26),
        OnsetMs = At(T0).Ms, LastRiseMs = At(T0.AddMinutes(26)).Ms, EndMs = At(T0.AddMinutes(26)).Ms,
        StartBytes = 95 * MB, PeakBytes = 675 * MB, CurrentBytes = 675 * MB,
    };

    [Fact]
    public void TraceOutranksCorrelationAndExplainsPerCommand()
    {
        var (reg, cap) = Session(shortCommands: 40);
        var inc = Inc();
        inc.Capture = cap;
        inc.Traced = TraceAttribution.Resolve(cap, Reff, reg);
        Assert.All(inc.Traced, t => Assert.True(t.Resolved));
        inc.TraceOwners = TraceAttribution.Owners(inc.Traced);
        var top = Assert.Single(inc.TraceOwners);
        Assert.Equal(OwnerKind.ClaudeSession, top.Owner.Kind);
        Assert.Equal(2, top.Big.Count);
        Assert.Equal(700 * KB, top.PerShortCommand);
        top.StartsInWindow = 412;
        var info = new TagInfo("refs.sys", "FCB_DATA", "test");
        string line = TraceAttribution.Answer(inc, info, inc.TraceOwners, cap)!;
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min and is still held. The trace shows shell commands (grep, cut, git, date", line);
        Assert.Contains("+147 MB from two commands in 1 min, and about 700 KB per short command, none freed.", line);
        Assert.Contains("Started by: Claude Code session sample-app (PID 4000).", line);
        Assert.EndsWith("412 short commands x 700 KB = 281.6 MB.", line);
        // Every traced process carries its identity, command line and chain.
        var grep = inc.Traced.Single(t => t.Identity.Image == "grep.exe");
        Assert.Equal("grep -rn TODO .", grep.CommandLine);
        Assert.Contains("claude.exe", grep.Chain[0]);
        Assert.Equal(3, grep.Chain.Count);
    }

    [Theory]
    // Still climbing after PoolWatch's own clear freed 200 MB of it: its real level is its highest now (the peak counts the
    // drop back, as Rank leaves it); the 200 MB is the clear's, not "freed since", and the rest is still held.
    [InlineData(875, 675, 200, true, "Reff (FCB_DATA) grew 780 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and the rest is still held. The trace")]
    // It also fell 100 MB on its own since its peak: each part is said as what it was.
    [InlineData(875, 575, 200, true, "Reff (FCB_DATA) grew 780 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and 100 MB more has been freed since. The trace")]
    // Two clears freed all of it and the 95 MB it held before (no one time to say): more than it grew. Its level is said, not
    // that none of the growth is held: PoolWatch cannot tell whose bytes a purge freed.
    [InlineData(675, 0, 675, false, "Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clears freed 675 MB of the tag, more than it grew: it is now empty. The trace")]
    // One clear freed 550 MB of a 500 MB growth (the cache it emptied was there before): its level, 45 MB, is under where
    // the growth began. A leak the purge could not free may still be held whole, so only the level is said.
    [InlineData(595, 45, 550, true, "Reff (FCB_DATA) grew 500 MB in 26 min; PoolWatch's own cache clear at {0} freed 550 MB of the tag, more than it grew: it is now at 45 MB, under the 95 MB it grew from. The trace")]
    // Exactly as much as it grew: back where the growth began.
    [InlineData(675, 95, 580, true, "Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clear at {0} freed 580 MB of the tag, as much as it grew: it is now back at the 95 MB it grew from. The trace")]
    // A level over where it began (the stitch does not make one, but the text stays true for any input): said as over.
    [InlineData(675, 120, 600, true, "Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clear at {0} freed 600 MB of the tag, more than it grew: it is now at 120 MB, over the 95 MB it grew from. The trace")]
    // A clear freed 200 MB of a 300 MB growth and 15 MB of the other 100 MB fell on its own: decided on the rise (15% of
    // what the clear left), not on the level (still over 90% of its 195 MB).
    [InlineData(395, 180, 200, true, "Reff (FCB_DATA) grew 300 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and 15 MB more has been freed since. The trace")]
    // Exactly a tenth of what the clear left fell on its own: still said as held (a tenth is the line).
    [InlineData(395, 185, 200, true, "Reff (FCB_DATA) grew 300 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and the rest is still held. The trace")]
    public void TheTraceAnswerSaysWhatPoolWatchsOwnClearsFreedAsTheirResult(long peakMb, long currentMb, long clearedMb, bool oneClear, string starts)
    {
        var (reg, cap) = Session(shortCommands: 0);
        var clearUtc = T0.AddMinutes(12);
        var inc = Inc();
        inc.PeakBytes = peakMb * MB;
        inc.CurrentBytes = currentMb * MB;
        inc.ClearedBytes = clearedMb * MB;
        inc.ClearedAtUtc = oneClear ? clearUtc : null;
        (inc.EndUtc, inc.EndMs) = (null, null);
        inc.Capture = cap;
        inc.Traced = TraceAttribution.Resolve(cap, Reff, reg);
        inc.TraceOwners = TraceAttribution.Owners(inc.Traced);
        string line = TraceAttribution.Answer(inc, new TagInfo("refs.sys", "FCB_DATA", "test"), inc.TraceOwners, cap)!;
        Assert.StartsWith(string.Format(System.Globalization.CultureInfo.InvariantCulture, starts, Fmt.Clock(clearUtc)), line);
    }

    /// <summary>
    /// The trace's answer for the incident with a level of <paramref name="currentMb"/> after a peak of
    /// <paramref name="peakMb"/>, of which PoolWatch's own clears freed <paramref name="clearedMb"/> (one clear, at
    /// 06:46, or several); open, or <paramref name="closed"/> at 07:04; its level read from the sample of
    /// <paramref name="readUtc"/> (null: never read from one).
    /// </summary>
    private static string Answer(long peakMb, long currentMb, long clearedMb, bool oneClear, DateTime? readUtc, bool closed)
    {
        var (reg, cap) = Session(shortCommands: 0);
        var inc = Inc();
        inc.PeakBytes = peakMb * MB;
        inc.CurrentBytes = currentMb * MB;
        inc.ClearedBytes = clearedMb * MB;
        inc.ClearedAtUtc = oneClear ? T0.AddMinutes(12) : null;
        (inc.EndUtc, inc.EndMs) = (null, null);
        if (closed) (inc.ClosedUtc, inc.ClosedMs) = (T0.AddMinutes(30), At(T0.AddMinutes(30)).Ms);
        inc.CurrentAtUtc = readUtc;
        inc.Capture = cap;
        inc.Traced = TraceAttribution.Resolve(cap, Reff, reg);
        inc.TraceOwners = TraceAttribution.Owners(inc.Traced);
        return TraceAttribution.Answer(inc, new TagInfo("refs.sys", "FCB_DATA", "test"), inc.TraceOwners, cap)!;
    }

    [Theory]
    // No clear: held, or part of it freed by the time the level was read.
    [InlineData(675, 675, 0, false, "Reff (FCB_DATA) grew 580 MB in 26 min and was still held at {1}. The trace")]
    [InlineData(675, 600, 0, false, "Reff (FCB_DATA) grew 580 MB in 26 min; 75 MB had been freed by {1}. The trace")]
    // PoolWatch's own clears freed part of it: the rest held, or more freed on its own.
    [InlineData(875, 675, 200, true, "Reff (FCB_DATA) grew 780 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and the rest was still held at {1}. The trace")]
    [InlineData(875, 575, 200, true, "Reff (FCB_DATA) grew 780 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and 100 MB more had been freed by {1}. The trace")]
    [InlineData(395, 180, 200, true, "Reff (FCB_DATA) grew 300 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and 15 MB more had been freed by {1}. The trace")]
    [InlineData(395, 185, 200, true, "Reff (FCB_DATA) grew 300 MB in 26 min; PoolWatch's own cache clear at {0} freed 200 MB of it, and the rest was still held at {1}. The trace")]
    // The clears freed as much as it grew, or more: its level, against where it grew from.
    [InlineData(675, 0, 675, false, "Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clears freed 675 MB of the tag, more than it grew: it was empty at {1}. The trace")]
    [InlineData(595, 45, 550, true, "Reff (FCB_DATA) grew 500 MB in 26 min; PoolWatch's own cache clear at {0} freed 550 MB of the tag, more than it grew: it was at 45 MB at {1}, under the 95 MB it grew from. The trace")]
    [InlineData(675, 95, 580, true, "Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clear at {0} freed 580 MB of the tag, as much as it grew: it was back at the 95 MB it grew from, at {1}. The trace")]
    [InlineData(675, 120, 600, true, "Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clear at {0} freed 600 MB of the tag, more than it grew: it was at 120 MB at {1}, over the 95 MB it grew from. The trace")]
    public void TheTraceAnswerOfAClosedIncidentSaysTheTimeItsLevelIsOf(long peakMb, long currentMb, long clearedMb, bool oneClear, string starts)
    {
        // A closed incident's level is as it was last read (at its close, or when it was found still held after it),
        // and its trace's answer is written again with its folder hours later, and shown in the window for an hour:
        // what it says of the level is in the past tense, with the time of the sample the level is of. (Before, it said
        // "and is still held", "has been freed since" and "it is now empty" of a level hours old, beside a Growth line
        // that gave the same level with its time.)
        var read = T0.AddMinutes(29).AddSeconds(50); // the last sample before its close at 07:04
        string line = Answer(peakMb, currentMb, clearedMb, oneClear, read, closed: true);
        Assert.StartsWith(string.Format(System.Globalization.CultureInfo.InvariantCulture, starts, Fmt.Clock(T0.AddMinutes(12)), Fmt.Clock(read)), line);
        Assert.DoesNotContain(" is still held", line);
        Assert.DoesNotContain("freed since", line);
        Assert.DoesNotContain("it is now", line);
    }

    [Fact]
    public void TheTraceAnswerSaysNowOnlyOfAnOpenIncidentAndNoLevelOfAClosedOneThatWasNeverRead()
    {
        // While the incident is open the level is the newest sample's: the present tense, whatever time it carries.
        var read = T0.AddMinutes(29).AddSeconds(50);
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min and is still held. The trace", Answer(675, 675, 0, false, read, closed: false));
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min; 75 MB has been freed since. The trace", Answer(675, 600, 0, false, read, closed: false));
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clears freed 675 MB of the tag, more than it grew: it is now empty. The trace",
            Answer(675, 0, 675, false, null, closed: false));
        // A closed incident whose level was never read from a sample has no time to give it with: its answer says what
        // grew and what the clears freed, and nothing of what was held (its Growth line says no level either).
        string clear = Fmt.Clock(T0.AddMinutes(12));
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min. The trace", Answer(675, 675, 0, false, null, closed: true));
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min. The trace", Answer(675, 600, 0, false, null, closed: true));
        Assert.StartsWith($"Reff (FCB_DATA) grew 780 MB in 26 min; PoolWatch's own cache clear at {clear} freed 200 MB of it. The trace", Answer(875, 675, 200, true, null, closed: true));
        Assert.StartsWith($"Reff (FCB_DATA) grew 780 MB in 26 min; PoolWatch's own cache clear at {clear} freed 200 MB of it. The trace", Answer(875, 575, 200, true, null, closed: true));
        Assert.StartsWith("Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clears freed 675 MB of the tag, more than it grew. The trace", Answer(675, 0, 675, false, null, closed: true));
        Assert.StartsWith($"Reff (FCB_DATA) grew 580 MB in 26 min; PoolWatch's own cache clear at {clear} freed 580 MB of the tag, as much as it grew. The trace", Answer(675, 95, 580, true, null, closed: true));
    }

    [Fact]
    public void TracedPidIsMatchedByCreateTimeNotJustPid()
    {
        var reg = new ProcessRegistry();
        reg.OnStart(700, 1, Ft(T0), "grep.exe", @"C:\x\grep.exe", "grep old", null, T0);
        reg.OnStop(700, Ft(T0), 0, T0.AddSeconds(2));
        reg.OnStart(700, 1, Ft(T0.AddMinutes(3)), "notepad.exe", @"C:\Windows\notepad.exe", "notepad", null, T0.AddMinutes(3)); // PID reused
        Assert.Equal("grep old", reg.FindInstance(700, Ft(T0), T0.AddMinutes(5))!.CommandLine);
        Assert.Equal("notepad", reg.FindInstance(700, 0, T0.AddMinutes(4))!.CommandLine);   // unknown create time: the one alive then
        Assert.Equal("grep old", reg.FindInstance(700, 0, T0.AddSeconds(1))!.CommandLine);
        Assert.Null(reg.FindInstance(701, 0, T0));
        // A trace row PoolWatch never saw keeps the trace's own command line, under its parent.
        var cap = new CaptureResult { PoolEventsParsed = true, StartUtc = T0, EndUtc = T0.AddMinutes(1), StartMs = Clocks.At(T0).Ms, EndMs = Clocks.At(T0.AddMinutes(1)).Ms, Tags = [Reff],
            Pool = [new(9999, "cut.exe", Reff, 5 * MB, 0, 10, 0, [], 0, "cut -f2", 700, T0.AddSeconds(1))] };
        var t = Assert.Single(TraceAttribution.Resolve(cap, Reff, reg));
        Assert.False(t.Resolved);
        Assert.Equal("cut -f2", t.CommandLine);
        Assert.Equal(2, t.Chain.Count); // grep (the parent alive at that time), then cut
    }

    [Fact]
    public void RecordsOfAnOpenIncidentWindowAreNotPruned()
    {
        ProcessRegistry Run(DateTime? protect)
        {
            // The registry reads the times onto its forward clock as they arrive (the real one here): so does the window.
            var reg = new ProcessRegistry { KeepShortLived = TimeSpan.FromMinutes(15), ProtectSinceMs = protect is { } p ? Moment.Now().MsOf(p) : null };
            reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0);
            for (int i = 0; i < 400; i++)
            {
                var t = T0.AddSeconds(i);
                reg.OnStart(10_000 + i, 4, Ft(t), "git.exe", null, "git status", null, t);
                reg.OnStop(10_000 + i, Ft(t), 0, t.AddSeconds(1));
            }
            reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0.AddMinutes(40));
            return reg;
        }
        Assert.Null(Run(null).FindInstance(10_000, 0, T0));
        Assert.NotNull(Run(T0.AddMinutes(-10)).FindInstance(10_000, 0, T0));
    }

    [Fact]
    public void ProtectedRecordsAreSizedByTimeNotByACount()
    {
        // While a trace records or parses (ProtectAll), every short command of the window stays resolvable, however many: the
        // newest keep their full record, older ones a compact start record (identity, parent, path; the trace carries the
        // command line itself).
        var reg = new ProcessRegistry { KeepShortLived = TimeSpan.FromMinutes(15), ProtectSinceMs = Moment.Now().MsOf(T0.AddMinutes(-10)), ProtectAll = true };
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0);
        var claude = reg.OnStart(4000, 4, Ft(T0.AddHours(-1)), "claude.exe", null, "claude", null, T0.AddHours(-1));
        int n = ProcessRegistry.MaxProtectedShortLived + 500;
        for (int i = 0; i < n; i++)
        {
            var t = T0.AddMilliseconds(i * 100);
            reg.OnStart(10_000 + i, 4000, Ft(t), "grep.exe", @"C:\Program Files\Git\usr\bin\grep.exe", "grep x", null, t);
            reg.OnStop(10_000 + i, Ft(t), 0, t.AddMilliseconds(50));
        }
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }, new() { Pid = 4000, ParentPid = 4, CreateTime = claude.Key.CreateTime, ImageName = "claude.exe" }], T0.AddMinutes(40));
        var oldest = reg.FindInstance(10_000, Ft(T0), T0);
        Assert.NotNull(oldest);                                       // past the old 3,000 cap, still inside the window
        Assert.Equal(@"C:\Program Files\Git\usr\bin\grep.exe", oldest!.ImagePath);
        Assert.Null(oldest.CommandLine);                              // compact: no command line
        Assert.Equal(["claude.exe", "grep.exe"], reg.Ancestry(oldest.Key).Select(r => r.Image)); // the chain reaches its owner
        Assert.Equal(500, reg.CompactCount);
        Assert.Equal("grep x", reg.FindInstance(10_000 + n - 1, 0, T0.AddMilliseconds((n - 1) * 100))!.CommandLine); // the newest stay full

        // The window ends: records that exited in it are no longer kept; the compact ones go with it.
        reg.ProtectSinceMs = null;
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }, new() { Pid = 4000, ParentPid = 4, CreateTime = claude.Key.CreateTime, ImageName = "claude.exe" }], T0.AddMinutes(42));
        Assert.Equal(0, reg.CompactCount);
        Assert.Null(reg.FindInstance(10_000, Ft(T0), T0));
    }

    [Fact]
    public void TheProtectedWindowEndsTenMinutesAfterAClosedIncident()
    {
        var reg = new ProcessRegistry
        {
            KeepShortLived = TimeSpan.FromMinutes(15), ProtectSinceMs = Moment.Now().MsOf(T0.AddMinutes(-10)), ProtectUntilMs = Moment.Now().MsOf(T0.AddMinutes(10)),
        };
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0);
        for (int i = 0; i < 400; i++)
        {
            var t = T0.AddSeconds(i * 6); // 0 .. 40 min
            reg.OnStart(10_000 + i, 4, Ft(t), "git.exe", null, "git status", null, t);
            reg.OnStop(10_000 + i, Ft(t), 0, t.AddSeconds(1));
        }
        reg.ObserveSnapshot([new() { Pid = 4, ImageName = "System" }], T0.AddMinutes(70));
        Assert.NotNull(reg.FindInstance(10_000, 0, T0));                     // inside the window
        Assert.Null(reg.FindInstance(10_000 + 300, 0, T0.AddMinutes(30)));  // after it: the normal 15-minute rule
    }

    [Fact]
    public void NoQuestionMarkForAProcessPoolWatchNoLongerHolds()
    {
        Assert.Matches(@"^unknown process \(started \d\d:\d\d:\d\d, exited \d\d:\d\d:\d\d\)$", TracedProcess.UnknownName(T0, T0.AddMinutes(1)));
        Assert.Matches(@"^unknown process \(started \?\)$", TracedProcess.UnknownName(null, null));
    }

    [Fact]
    public void EventWindowCoversTheGrowthNotTheClose()
    {
        var inc = Inc();
        (inc.ClosedUtc, inc.ClosedMs) = (T0.AddMinutes(47), At(T0.AddMinutes(47)).Ms);
        Assert.Equal(At(T0.AddMinutes(-2)).Ms, IncidentManager.EventWindowStartMs(inc));
        Assert.Equal(At(T0.AddMinutes(27)).Ms, IncidentManager.EventWindowEndMs(inc, At(T0.AddHours(2)).Ms));
        (inc.EndUtc, inc.EndMs) = (null, null);
        (inc.ClosedUtc, inc.ClosedMs) = (null, null); // still open: up to the latest sample
        Assert.Equal(At(T0.AddMinutes(61)).Ms, IncidentManager.EventWindowEndMs(inc, At(T0.AddMinutes(60)).Ms));
    }

    [Fact]
    public void RepeatedGrowthReopensTheSameIncident()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-reopen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 0.5, RetentionMinutes = 60 };
            var w = new World();
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(dir), w.Registry) { RewriteInterval = TimeSpan.Zero };
            var det = new Detector(settings);
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, "leaker.exe");
            long tag = 50 * Settings.MB;
            void Step() { w.Sample(("VdMm", tag)); mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval)); }
            for (int i = 0; i < 15; i++) Step();
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 20; i++) { w.Io(100, other: 2000); tag += 800_000; Step(); }
                for (int i = 0; i < 40; i++) Step(); // quiet: closes
            }
            var all = mgr.All.Where(i => i.Tag == PoolTag.Parse("VdMm")).ToList();
            var inc = Assert.Single(all);
            Assert.Equal(2, inc.Reopened);
            Assert.Equal(2, inc.Timeline.Count(t => t.Text.StartsWith("re-opened")));
            Assert.StartsWith("opened", inc.Timeline[0].Text);
            Assert.Single(Directory.GetDirectories(dir));
            Assert.Contains("## Timeline", File.ReadAllText(Path.Combine(inc.Dir, "report.md")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class IncidentLifecycleTests
{
    [Fact]
    public void ReopenedIncidentDoesNotFlapOnTheMaximumDuration()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-flap-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Maximum 1.5 min: the first episode closes on it; the re-open must get its own 1.5 min, not close at once.
            var settings = new Settings { GrowthRules = [new(1, 3 * Settings.MB)], IncidentQuietMinutes = 5, IncidentMaxMinutes = 1.5, RetentionMinutes = 60 };
            var w = new World();
            var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(dir), w.Registry) { RewriteInterval = TimeSpan.Zero };
            var det = new Detector(settings);
            w.AddProcess(4, 0, "System");
            w.AddProcess(100, 4, "leaker.exe");
            long tag = 50 * Settings.MB;
            int opens = 0;
            mgr.Opened += (_, _) => opens++;
            void Step() { w.Sample(("Nbuf", tag)); mgr.Process(w.History, det, det.Evaluate(w.History), At(w.Now - w.Interval)); }
            for (int i = 0; i < 15; i++) Step();
            for (int i = 0; i < 120; i++) { w.Io(100, other: 2000); tag += 800_000; Step(); } // 4 min of steady growth
            var inc = Assert.Single(mgr.All, i => i.Tag == PoolTag.Parse("Nbuf"));
            int reopenCloses = inc.Timeline.Count(t => t.Text.StartsWith("re-opened"));
            Assert.InRange(reopenCloses, 1, 3); // about one re-open per maximum duration, not one per sample
            Assert.True(inc.Timeline.Count < 40, $"{inc.Timeline.Count} timeline entries");
            Assert.Equal(1 + inc.Reopened, opens); // each episode raises Opened (capture, notification)
            Assert.Contains(inc.Timeline, t => t.Text.StartsWith("closed: open for the maximum"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void OwnerFiguresAreComputedOnceAndFast()
    {
        var reg = new ProcessRegistry();
        var t0 = new DateTime(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc);
        reg.OnStart(10, 1, t0.AddHours(-1).ToFileTimeUtc(), "claude.exe", null, "claude", null, t0.AddHours(-1));
        var pool = new List<CapturePoolUsage>();
        for (int i = 0; i < 3000; i++)
        {
            var t = t0.AddMilliseconds(i * 20);
            reg.OnStart(1000 + i, 10, t.ToFileTimeUtc(), "git.exe", @"C:\Program Files\Git\mingw64\bin\git.exe", "git status", null, t);
            reg.OnStop(1000 + i, t.ToFileTimeUtc(), 0, t.AddMilliseconds(100));
            pool.Add(new CapturePoolUsage(1000 + i, "git.exe", PoolTag.Parse("Reff"), 700 << 10, 0, 540, 0, [], t.ToFileTimeUtc(), "git status", 10, t));
        }
        var cap = new CaptureResult { PoolEventsParsed = true, StartUtc = t0, EndUtc = t0.AddMinutes(1), StartMs = Clocks.At(t0).Ms, EndMs = Clocks.At(t0.AddMinutes(1)).Ms, Tags = [PoolTag.Parse("Reff")], Pool = pool };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var owners = TraceAttribution.Owners(TraceAttribution.Resolve(cap, PoolTag.Parse("Reff"), reg));
        for (int i = 0; i < 100; i++) _ = owners[0].PerShortCommand + owners[0].ShortOnes.Count + owners[0].Big.Count;
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(700 << 10, owners[0].PerShortCommand);
        Assert.Equal(3000, owners[0].ShortOnes.Count);
    }

    [Fact]
    public void TraceOnlyProcessesUseTheTracesExitTimeAndNoPidName()
    {
        var reg = new ProcessRegistry();
        var t0 = new DateTime(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc);
        var u = new CapturePoolUsage(4321, "PID 4321", PoolTag.Parse("Reff"), 700 << 10, 0, 540, 0, [], t0.ToFileTimeUtc(), null, 0, t0, t0.AddMilliseconds(300));
        var cap = new CaptureResult { PoolEventsParsed = true, StartUtc = t0.AddSeconds(-1), EndUtc = t0.AddMinutes(1), StartMs = Clocks.At(t0.AddSeconds(-1)).Ms, EndMs = Clocks.At(t0.AddMinutes(1)).Ms, Tags = [u.Tag], Pool = [u] };
        var t = Assert.Single(TraceAttribution.Resolve(cap, u.Tag, reg));
        Assert.True(t.Short);
        Assert.StartsWith("unknown process (started ", t.Name);
    }
}
