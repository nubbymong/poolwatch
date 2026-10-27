using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The Pool causes list (the main window's first tab): one row per incident, growing tag, process that stands out, and
/// pool-related finding. Only the pure mapping is tested here; nothing opens a window or reads the system.
/// </summary>
public class PoolCausesTests
{
    private const long MB = 1L << 20, GB = 1L << 30;
    private static readonly DateTime Now = new DateTime(2026, 10, 27, 15, 0, 0, DateTimeKind.Local).ToUniversalTime();
    private static readonly Regex Negative = new(@"(\+-|-\d|−)");

    private static IncidentRow Incident(IncidentKind kind, long growth, bool open, long dropped = 0) =>
        new("20261027-140000-File-656c6946", @"C:\data\incidents\20261027-140000-File-656c6946", Now.AddHours(-1), "File", kind, growth,
            "Leaker.exe (PID 7716)", "strong evidence", open)
        { Level = Confidence.StronglyCorrelated, OnsetUtc = Now.AddHours(-1), EndUtc = open ? null : Now.AddMinutes(-30), Dropped = dropped };

    private static TagStatus Tag(TagState state, long bytes, long d10, long d60, DateTime? since = null) =>
        new(PoolTag.Parse("Reff"), bytes, bytes, 0, 0, d10, d60, d60, state, since);

    [Fact]
    public void TagsReadInPlainWords()
    {
        // The row's "What is growing": an everyday name, then the tag code and driver, in one pair of brackets.
        Assert.Equal("Open files (File)", PoolCauses.TagWords(PoolTag.Parse("File"), new TagInfo("<unknown>", "File objects", "pooltag.txt")));
        Assert.Equal("File system metadata (Reff, refs.sys)", PoolCauses.TagWords(PoolTag.Parse("Reff"), BuiltinTags.Table[PoolTag.Parse("Reff")]));
        // A tag with no pooltag.txt data: found by the driver scan, or not at all; a report's tag read back as text.
        Assert.Equal("Pool tag Abcd (foo.sys)", PoolCauses.TagWords(PoolTag.Parse("Abcd"), new TagInfo("foo.sys", "", "driver scan")));
        Assert.Equal("Pool tag Abcd", PoolCauses.TagWords("Abcd", null));
        Assert.Equal("Pool tag toolong", PoolCauses.TagWords("toolong", null)); // not a tag: shown as it is
        Assert.Equal("Pool tag Abcd (foo.sys)", PoolCauses.TagWords(PoolTag.Parse("Abcd"), new TagInfo("foo.sys", new string('x', 61), "pooltag.txt"))); // too long to read
    }

    [Fact]
    public void RowsNeverNestBrackets()
    {
        var nested = new Regex(@"\([^()]*\(");
        foreach (var kind in Enum.GetValues<IncidentKind>())
            foreach (var cell in PoolCauses.ForIncident(Incident(kind, 300 * MB, open: true, dropped: 300 * MB), "File system metadata (Reff, refs.sys)", Now).Cells)
                Assert.DoesNotMatch(nested, cell);
        foreach (var state in new[] { TagState.Growing, TagState.Retained, TagState.Released })
            foreach (var cell in PoolCauses.ForTag(Tag(state, GB, 100 * MB, 300 * MB, Now.AddMinutes(-5)), "File system metadata (Reff, refs.sys)", null, Now).Cells)
                Assert.DoesNotMatch(nested, cell);
    }

    [Fact]
    public void AnOpenIncidentSaysHowMuchHowFastTheCauseAndSince()
    {
        var r = PoolCauses.ForIncident(Incident(IncidentKind.Growth, (long)(1.2 * GB), open: true), "Open files (File, Windows kernel)", Now);
        Assert.Equal(CauseKind.Incident, r.Kind);
        Assert.Equal(new[] { "Open files (File, Windows kernel)", "+1.2 GB", "1.2 GB/h", "Leaker.exe (PID 7716)", "strong evidence", "since 14:00" }, r.Cells);
        Assert.True(r.Active);
        Assert.Equal(RowTone.Alert, r.Tone);
        Assert.NotNull(r.Folder);

        var closed = PoolCauses.ForIncident(Incident(IncidentKind.Growth, 300 * MB, open: false), "File objects (File)", Now);
        Assert.False(closed.Active);
        Assert.Equal(RowTone.Normal, closed.Tone);
        Assert.Equal("14:00", closed.When);
        Assert.Equal("600 MB/h", closed.HowFast); // 300 MB from onset to end (30 min)
    }

    [Fact]
    public void AReleaseSaysFreedNeverANegativeGrowth()
    {
        var r = PoolCauses.ForIncident(Incident(IncidentKind.Release, -400 * MB, open: false, dropped: 400 * MB), "File objects (File)", Now);
        Assert.Equal("File objects (File) was released", r.What);
        Assert.Equal("freed 400 MB", r.HowMuch);
        Assert.Equal("", r.HowFast);
        Assert.DoesNotMatch(Negative, string.Join(" ", r.Cells));
    }

    [Fact]
    public void AGrowingTagWithoutARankingSaysHowToGetOne()
    {
        var r = PoolCauses.ForTag(Tag(TagState.Growing, (long)(1.2 * GB), 100 * MB, 300 * MB), "FCB_DATA (Reff, refs.sys)", view: null, Now);
        Assert.Equal(CauseKind.Tag, r.Kind);
        Assert.Equal("+300 MB (now 1.2 GB)", r.HowMuch);
        Assert.Equal("600 MB/h", r.HowFast);
        Assert.Equal(PoolCauses.NotRanked, r.Cause);
        Assert.Equal("now", r.When);
        Assert.True(r.Active);
        Assert.Equal(RowTone.Warning, r.Tone);
    }

    [Fact]
    public void ReleasedAndRetainedTagsReadPlainly()
    {
        var released = PoolCauses.ForTag(Tag(TagState.Released, 100 * MB, -500 * MB, -450 * MB, Now.AddMinutes(-3)), "FCB_DATA (Reff, refs.sys)", null, Now);
        Assert.Equal("FCB_DATA (Reff, refs.sys) was released", released.What);
        Assert.Equal("freed 500 MB", released.HowMuch);
        Assert.Equal("", released.Cause);
        Assert.False(released.Active);
        Assert.DoesNotMatch(Negative, string.Join(" ", released.Cells));

        var retained = PoolCauses.ForTag(Tag(TagState.Retained, 700 * MB, 0, 0, Now.AddMinutes(-20)), "FCB_DATA (Reff, refs.sys)", null, Now);
        Assert.Equal("700 MB held", retained.HowMuch);
        Assert.Equal("not falling", retained.HowFast);
        Assert.Equal("since 14:40", retained.When);
    }

    private static ProcessCandidate Proc(int handles, int handlesBefore, long quota, long quotaBefore, bool orphan = false, long ops = 0) =>
        new(new ProcessKey(7716, Now.AddHours(-2).ToFileTimeUtc()), "Leaker.exe", handles, handlesBefore, quota, quotaBefore, TimeSpan.FromHours(1), orphan, ops, Now.AddHours(-2));

    [Fact]
    public void OnlyProcessesThatPileUpHandlesOrPoolOrCrawlAsOrphansStandOut()
    {
        Assert.True(PoolCauses.Stands(Proc(36_000, 30_000, 0, 0)));                 // +6,000 handles
        Assert.False(PoolCauses.Stands(Proc(34_999, 30_000, 0, 0)));                // +4,999: not enough
        Assert.False(PoolCauses.Stands(Proc(60_000, 60_000, 0, 0)));                // many handles, but not rising
        Assert.True(PoolCauses.Stands(Proc(10, 10, 100 * MB, 60 * MB)));            // +40 MB of pool charged to it
        Assert.False(PoolCauses.Stands(Proc(10, 10, 100 * MB, 90 * MB)));
        Assert.True(PoolCauses.Stands(Proc(10, 10, 0, 0, orphan: true, ops: 900))); // the orphaned crawl
        Assert.False(PoolCauses.Stands(Proc(10, 10, 0, 0, orphan: true, ops: 100)));
        Assert.False(PoolCauses.Stands(Proc(10, 10, 0, 0, orphan: false, ops: 5_000))); // busy, but its parent is alive
    }

    [Fact]
    public void AProcessRowSaysWhatRose()
    {
        var h = PoolCauses.ForProcess(Proc(36_000, 30_000, 0, 0), Now);
        Assert.Equal("Handles held by Leaker.exe", h.What);
        Assert.Equal($"+{6000:N0} (now {36000:N0})", h.HowMuch);
        Assert.Equal($"{6000:N0} handles/h", h.HowFast);
        Assert.Equal("Leaker.exe (PID 7716)", h.Cause);
        Assert.Equal("started 13:00", h.When);

        var q = PoolCauses.ForProcess(Proc(10, 10, 100 * MB, 60 * MB), Now);
        Assert.Equal("Pool charged to Leaker.exe", q.What);
        Assert.Equal("+40 MB (now 100 MB)", q.HowMuch);
        Assert.Equal("40 MB/h", q.HowFast);
    }

    [Fact]
    public void OnlyPoolRelatedFindingsAndCacheClearsAreListed()
    {
        Finding F(FindingKind kind, FindingSeverity severity, TimeSpan ago) => new() { TimeUtc = Now - ago, Kind = kind, Severity = severity, Title = $"{kind} happened" };
        var hour = TimeSpan.FromHours(1);
        Assert.True(PoolCauses.Shows(F(FindingKind.CacheClear, FindingSeverity.Info, hour), Now));
        Assert.True(PoolCauses.Shows(F(FindingKind.SpawnStorm, FindingSeverity.Alert, hour), Now));
        Assert.True(PoolCauses.Shows(F(FindingKind.ChildAccumulation, FindingSeverity.Warning, hour), Now));
        Assert.False(PoolCauses.Shows(F(FindingKind.SpawnStorm, FindingSeverity.Info, hour), Now));   // a storm that did not open
        Assert.False(PoolCauses.Shows(F(FindingKind.Lag, FindingSeverity.Alert, hour), Now));         // not a pool cause
        Assert.False(PoolCauses.Shows(F(FindingKind.Mark, FindingSeverity.Info, hour), Now));
        Assert.False(PoolCauses.Shows(F(FindingKind.VersionChange, FindingSeverity.Warning, hour), Now));
        Assert.False(PoolCauses.Shows(F(FindingKind.CacheClear, FindingSeverity.Info, TimeSpan.FromHours(25)), Now)); // older than a day
    }

    [Fact]
    public void ACacheClearRowSaysWhatItFreed()
    {
        var f = new Finding
        {
            TimeUtc = Now.AddMinutes(-5), Kind = FindingKind.CacheClear, Title = "Clear cache (standby list, manual): freed 4.7 GB of pool (paged 6.2 GB -> 3.6 GB)",
            Data = new Dictionary<string, object?> { ["freedBytes"] = (long)(4.7 * GB), ["error"] = null },
        };
        var r = PoolCauses.ForFinding(f, Now);
        Assert.Equal("Clear cache (standby list, manual)", r.What);
        Assert.Equal("freed 4.7 GB", r.HowMuch);
        Assert.Equal("14:55", r.When);
        Assert.False(r.Active);
    }

    [Fact]
    public void WhatIsHappeningNowComesFirst()
    {
        var closed = PoolCauses.ForIncident(Incident(IncidentKind.Growth, 300 * MB, open: false), "a", Now);
        var finding = PoolCauses.ForFinding(new Finding { TimeUtc = Now.AddMinutes(-1), Kind = FindingKind.SpawnStorm, Severity = FindingSeverity.Alert, Title = "storm" }, Now);
        var process = PoolCauses.ForProcess(Proc(36_000, 30_000, 0, 0), Now);
        var tag = PoolCauses.ForTag(Tag(TagState.Growing, GB, 100 * MB, 300 * MB), "b", null, Now);
        var open = PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: true), "c", Now);
        var order = PoolCauses.Order([closed, finding, process, tag, open]);
        Assert.Equal(new[] { open, tag, process, finding, closed }, order);
    }

    [Fact]
    public void NoRowEverShowsANegativeAmount()
    {
        long[] amounts = [-2 * GB, -300 * MB, -1, 0, 1, 300 * MB, 2 * GB];
        foreach (var a in amounts)
        foreach (var b in amounts)
        {
            foreach (var state in new[] { TagState.Growing, TagState.Retained, TagState.Released })
                Assert.DoesNotMatch(Negative, string.Join(" ", PoolCauses.ForTag(Tag(state, Math.Abs(a), a, b), "x", null, Now).Cells));
            foreach (var kind in Enum.GetValues<IncidentKind>())
                Assert.DoesNotMatch(Negative, string.Join(" ", PoolCauses.ForIncident(Incident(kind, a, open: b > 0, dropped: Math.Abs(b)), "x", Now).Cells));
        }
    }
}
