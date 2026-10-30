using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Identity;
using PoolWatch.Core.Impact;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Tags;
using PoolWatch.Ui;

namespace PoolWatch.AppTests;

/// <summary>
/// The Pool causes list (the main window's first tab): one row per incident, growing tag, process that stands out, and
/// pool-related finding. Only the pure mapping is tested here; nothing opens a window or reads the system.
/// </summary>
[Collection(nameof(WholeWindowLayout))] // lays windows out: one class at a time, after the parallel ones
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
        // With no window stats for its tag, Grew and Per hour say the incident's own growth and rate.
        Assert.Equal(new[] { "Open files (File, Windows kernel)", "+1.2 GB", "", "", "1.2 GB/h", "Leaker.exe (PID 7716)", "strong evidence", "High", "since 14:00" }, r.Cells);
        Assert.Equal("Open files (File, Windows kernel): +1.2 GB", r.Title);
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
    public void AnIncidentRowKnowsItsTagSoTheKnownIssuesAboutItCanBeShown()
    {
        Assert.Equal(PoolTag.Parse("File"), PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: true), "a", Now).Tag);
        // A tag read back from an older report that does not parse names no known issue.
        Assert.Null(PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: true) with { Tag = "not a tag" }, "a", Now).Tag);
    }

    [Fact]
    public void AReleaseSaysFreedNeverANegativeGrowth()
    {
        var r = PoolCauses.ForIncident(Incident(IncidentKind.Release, -400 * MB, open: false, dropped: 400 * MB), "File objects (File)", Now);
        Assert.Equal("File objects (File) was released", r.What);
        Assert.Equal("freed 400 MB", r.HowMuch);
        Assert.Equal("", r.HowFast);
        // Never a negative growth: what fell is its net change, with the true minus sign.
        Assert.Equal(("", "\u2212400 MB"), (r.Grew, r.Net));
        Assert.Equal(-400 * MB, r.NetBytes);
        AssertOnlyTheNetChangeIsSigned(r);
        Assert.Equal("File objects (File) was released: freed 400 MB", r.Title);
    }

    /// <summary>A tag's ranking with one owner at <paramref name="level"/> (or none that stood out).</summary>
    private static TagViewData View(Confidence? level, long growth = 300 * MB)
    {
        var owners = level is { } l
            ? new List<OwnerSuspect> { new() { Owner = new Core.Identity.Owner(Core.Identity.OwnerKind.App, "leaker", "Leaker"), Members = [], Identities = [], Confidence = l, Meaningful = true } }
            : [];
        var tag = PoolTag.Parse("Reff");
        return new TagViewData(tag, "Reff", new TagAnswer("Reff grew.", owners, growth, 0), owners, [], Now.AddHours(-1), Now, Now.AddMinutes(-30));
    }

    [Fact]
    public void AGrowingTagWithoutARankingYetLeavesItsCauseBlankNeverAPlaceholder()
    {
        var r = PoolCauses.ForTag(Tag(TagState.Growing, (long)(1.2 * GB), 100 * MB, 300 * MB), "File system metadata (Reff, refs.sys)", view: null, Now);
        Assert.Equal(CauseKind.Tag, r.Kind);
        Assert.Equal("+300 MB (now 1.2 GB)", r.HowMuch);
        Assert.Equal("600 MB/h", r.HowFast);
        Assert.Equal("", r.Cause);      // blank until the background ranking arrives
        Assert.Equal("", r.Confidence);
        Assert.Equal("now", r.When);
        Assert.True(r.Active);
        Assert.Equal(RowTone.Warning, r.Tone);
    }

    [Fact]
    public void ARankingFillsTheCauseAndTheConfidence()
    {
        var growing = Tag(TagState.Growing, GB, 100 * MB, 300 * MB);
        var r = PoolCauses.ForTag(growing, "File system metadata (Reff, refs.sys)", View(Confidence.StronglyCorrelated), Now);
        Assert.Equal("Leaker", r.Cause);
        Assert.Equal("strong evidence", r.Confidence);
        // Ranked, but nobody stood out.
        var none = PoolCauses.ForTag(growing, "x", View(null), Now);
        Assert.Equal("no clear cause yet", none.Cause);
        Assert.Equal("", none.Confidence);
        // A retained tag is ranked like a growing one; a released tag has no cause to name.
        Assert.Equal("Leaker", PoolCauses.ForTag(Tag(TagState.Retained, GB, 0, 0, Now), "x", View(Confidence.Correlated), Now).Cause);
        Assert.Equal("", PoolCauses.ForTag(Tag(TagState.Released, GB, -500 * MB, -500 * MB, Now), "x", View(Confidence.Correlated), Now).Cause);
        // No cell anywhere asks the user to do something to see a cause.
        foreach (var view in new[] { null, View(null), View(Confidence.Weak) })
            foreach (var state in new[] { TagState.Growing, TagState.Retained, TagState.Released })
                Assert.DoesNotContain(PoolCauses.ForTag(Tag(state, GB, 1, 1, Now), "x", view, Now).Cells, c => c.Contains("select", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The services Windows runs now, with the names it shows for them.</summary>
    private static readonly ServiceEntry[] Running = [new("ExampleDeviceService", "Example Device Service", 5120), new("Schedule", "Task Scheduler", 1400)];

    private static ProcessRecord P(string image, params string[] services)
    {
        var r = new ProcessRecord { Key = new ProcessKey(5120, 1), Image = image };
        r.Services.AddRange(services);
        return r;
    }

    private static Suspect Process(string image, params string[] services) => new()
    {
        Key = new ProcessKey(7716, 1), Image = image, DisplayName = image, Services = [.. services],
        Direct = new DirectSignal(null, 0, 0, 0, 0, 0, false), Activity = new ActivitySignal("ops", 0, 0, 0, 0, 0, false),
        Lifecycle = new LifecycleSignal(false, false, false, []),
    };

    private static OwnerSuspect Ranked(Owner owner, Confidence level, bool meaningful = true) =>
        new() { Owner = owner, Members = [], Identities = [], Confidence = level, Meaningful = meaningful };

    /// <summary>An open growth incident of the File tag with what its ranking found so far.</summary>
    private static Incident Open(IncidentKind kind = IncidentKind.Growth, List<Suspect>? suspects = null, List<OwnerSuspect>? owners = null, List<TraceOwner>? traced = null) =>
        new() { Id = "20261027-140000-File-656c6946", Dir = @"C:\data\incidents\x", Tag = PoolTag.Parse("File"), Kind = kind, Suspects = suspects ?? [], Owners = owners, TraceOwners = traced };

    /// <summary>A process known by its exe and PID only: its name in the ranking is never what the status line or the tray says.</summary>
    private static readonly Owner LeakerByPid = new(OwnerKind.Unknown, "proc:7716", "Leaker.exe (PID 7716)");

    [Fact]
    public void ACauseIsNamedAsPeopleKnowItNeverByItsExeOrPid()
    {
        var running = Running;
        // A service by the name Windows shows ("ExampleDeviceService.exe (ExampleDeviceService)" before), else its own.
        var device = ProcessIdentities.OwnerOf([P("services.exe"), P("ExampleDeviceService.exe", "ExampleDeviceService")]);
        Assert.Equal(OwnerKind.Service, device.Kind);
        Assert.Equal("Example Device Service", PoolCauses.FriendlyName(device, running));
        Assert.Equal("ExampleDeviceService", PoolCauses.FriendlyName(device, []));
        Assert.Equal("Task Scheduler and 2 other services", PoolCauses.FriendlyName(ProcessIdentities.OwnerOf([P("svchost.exe", "Schedule", "BITS", "Winmgmt")]), running));
        Assert.Equal("Task Scheduler and 1 other service", PoolCauses.FriendlyName(ProcessIdentities.OwnerOf([P("svchost.exe", "Schedule", "BITS")]), running));
        // An app by its own name, a Claude Code session by its folder, any other program without ".exe" or its PID.
        Assert.Equal("Stream Deck", PoolCauses.FriendlyName(new Owner(OwnerKind.App, "app:stream deck", "Stream Deck"), running));
        Assert.Equal("Claude Code session sample-app", PoolCauses.FriendlyName(new Owner(OwnerKind.ClaudeSession, "claude:1", "Claude Code session sample-app (PID 7716)"), running));
        Assert.Equal("node", PoolCauses.FriendlyName(new Owner(OwnerKind.Unknown, "proc:1", "node.exe (PID 7716)"), running));
        Assert.Equal("Leaker", PoolCauses.FriendlyName(Process("Leaker.exe"), running));
        Assert.Equal("Example Device Service", PoolCauses.FriendlyName(Process("ExampleDeviceService.exe", "ExampleDeviceService"), running));
        // Nothing anyone would know by a name: none is said (Pool causes says what they are).
        Assert.Null(PoolCauses.FriendlyName(new Owner(OwnerKind.System, "system", "the kernel (System worker threads)"), running));
        Assert.Null(PoolCauses.FriendlyName(new Owner(OwnerKind.ViaWmi, "wmi:1", "a WMI-launched foo.exe"), running));
        Assert.Null(PoolCauses.FriendlyName(new Owner(OwnerKind.Unknown, "pid:12", "PID 12"), running));
        Assert.Null(PoolCauses.FriendlyName(Process("unknown process"), running));
    }

    [Fact]
    public void TheStatusLineNamesAnIncidentsCauseAsPeopleKnowIt()
    {
        // In the order Pool causes picks it: the trace's top owner, else the top owner that stands out, else the top process.
        var device = ProcessIdentities.OwnerOf([P("services.exe"), P("ExampleDeviceService.exe", "ExampleDeviceService")]);
        var traced = Open(traced: [new TraceOwner { Owner = device }], owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]);
        Assert.Equal(("Example Device Service", Confidence.ConfirmedByTrace), PoolCauses.FriendlyCause(traced, Running));

        var app = Open(owners: [Ranked(LeakerByPid, Confidence.Correlated, meaningful: false), Ranked(new Owner(OwnerKind.App, "app:stream deck", "Stream Deck"), Confidence.StronglyCorrelated)],
            suspects: [Process("Leaker.exe")]);
        Assert.Equal(("Stream Deck", Confidence.StronglyCorrelated), PoolCauses.FriendlyCause(app, Running));

        var byPid = Open(owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]);
        Assert.Equal(("Leaker", Confidence.StronglyCorrelated), PoolCauses.FriendlyCause(byPid, Running)); // never "Leaker.exe (PID 7716)"

        var suspect = Process("Leaker.exe");
        suspect.Confidence = Confidence.Correlated;
        Assert.Equal(("Leaker", Confidence.Correlated), PoolCauses.FriendlyCause(Open(suspects: [suspect]), Running)); // never "Leaker.exe (PID 7716)"
        Assert.Null(PoolCauses.FriendlyCause(Open(), Running));
    }

    [Fact]
    public void TheStatusLineNamesAGrowingTagsCauseAsPeopleKnowIt()
    {
        var owners = new List<OwnerSuspect> { Ranked(LeakerByPid, Confidence.StronglyCorrelated) };
        var view = new TagViewData(PoolTag.Parse("Reff"), "Reff", new TagAnswer("Reff grew.", owners, 300 * MB, 0), owners, [], Now.AddHours(-1), Now, Now.AddMinutes(-30));
        Assert.Equal(("Leaker", Confidence.StronglyCorrelated), PoolCauses.TagCause(view, Running)); // the ranking's own name has the exe and PID
        Assert.Equal(("Leaker", Confidence.ConfirmedByTrace), PoolCauses.TagCause(view with { TraceBacked = true }, Running));
        Assert.Null(PoolCauses.TagCause(null, Running)); // not ranked yet
        Assert.Null(PoolCauses.TagCause(view with { Answer = view.Answer with { Growth = 0 } }, Running));
    }

    [Fact]
    public void TheTraysIncidentNoticeSaysKernelPoolAndNamesACauseOnlyAtStrongEvidence()
    {
        var info = new TagInfo("<unknown>", "File objects", "pooltag.txt");
        var strong = Open(owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)], suspects: [Process("Leaker.exe")]);
        Assert.Equal("Kernel pool: Open files (File) is growing. PoolWatch is keeping an eye on Leaker.", PoolCauses.IncidentNotice(strong, info, Running));

        // A weaker suspect is not named, as on the status line: growth often goes away by itself.
        var weak = Process("Leaker.exe");
        weak.Confidence = Confidence.Correlated;
        Assert.Equal("Kernel pool: Open files (File) is growing. Open PoolWatch for details.", PoolCauses.IncidentNotice(Open(suspects: [weak]), info, Running));
        Assert.Equal("Kernel pool: Open files (File) is growing. Open PoolWatch for details.", PoolCauses.IncidentNotice(Open(), info, Running));
        // A cause nobody knows by a name is not named either.
        var system = Open(owners: [Ranked(new Owner(OwnerKind.System, "system", "the kernel (System worker threads)"), Confidence.StronglyCorrelated)]);
        Assert.EndsWith("is growing. Open PoolWatch for details.", PoolCauses.IncidentNotice(system, info, Running));

        string? total = PoolCauses.IncidentNotice(Open(IncidentKind.NonPagedTotal, traced: [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:codex", "Codex") }]), info, Running);
        Assert.Equal("Kernel pool: Open files (File) is growing, with non-paged pool over the limit. PoolWatch is keeping an eye on Codex.", total);
        foreach (var text in new[] { PoolCauses.IncidentNotice(strong, info, Running)!, total! })
        {
            Assert.DoesNotMatch(new Regex(@"\.exe\b|\(PID \d+\)|>"), text); // no exe, no PID, no detector rule
            Assert.True(text.Length <= 250, text); // what a notification shows
        }

        // A pool released (a tag that dropped with no growth incident, often after PoolWatch's own clear) is no alarm:
        // no notice at all, however strong its ranking, and so never "is growing" or a cause.
        Assert.Null(PoolCauses.IncidentNotice(Open(IncidentKind.Release, owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]), info, Running));
        Assert.Null(PoolCauses.IncidentNotice(Open(IncidentKind.Release, traced: [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:codex", "Codex") }]), info, Running));
        Assert.Null(PoolCauses.IncidentNotice(Open(IncidentKind.Capture), info, Running));
    }

    [Fact]
    public void TheTrayWordsAnOpenedIncidentWithTheTagsDescriptionAndLogsAFailure()
    {
        var info = new TagInfo("<unknown>", "File objects", "pooltag.txt");
        var log = new List<string>();
        var asked = new List<PoolTag>();
        var strong = Open(owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]);
        Assert.Equal("Kernel pool: Open files (File) is growing. PoolWatch is keeping an eye on Leaker.",
            PoolCauses.TrayNotice(strong, t => { asked.Add(t); return info; }, Running, log.Add)); // never the bare tag code
        Assert.Equal([PoolTag.Parse("File")], asked);
        Assert.Null(PoolCauses.TrayNotice(Open(IncidentKind.Release, owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]), _ => info, Running, log.Add));
        Assert.Empty(log);
        Assert.Null(PoolCauses.TrayNotice(strong, _ => throw new InvalidOperationException("no knowledge"), Running, log.Add));
        Assert.Equal(["incident notice: no knowledge"], log);
    }

    [Fact]
    public void OnlyGrowingAndHeldTagRowsAreRanked()
    {
        var growing = PoolCauses.ForTag(Tag(TagState.Growing, GB, 100 * MB, 300 * MB), "a", null, Now);
        var released = PoolCauses.ForTag(Tag(TagState.Released, GB, -500 * MB, -500 * MB, Now), "b", null, Now) with { Tag = PoolTag.Parse("File") };
        var incident = PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: true), "c", Now);
        Assert.Equal(new[] { PoolTag.Parse("Reff") }, PoolCauses.TagsToRank([incident, growing, released, growing]));
    }

    [Fact]
    public void ReleasedAndRetainedTagsReadPlainly()
    {
        var released = PoolCauses.ForTag(Tag(TagState.Released, 100 * MB, -500 * MB, -450 * MB, Now.AddMinutes(-3)), "FCB_DATA (Reff, refs.sys)", null, Now);
        Assert.Equal("FCB_DATA (Reff, refs.sys) was released", released.What);
        Assert.Equal("freed 500 MB", released.HowMuch);
        Assert.Equal("", released.Cause);
        Assert.False(released.Active);
        Assert.Equal(("", "\u2212500 MB"), (released.Grew, released.Net));
        AssertOnlyTheNetChangeIsSigned(released);

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

    /// <summary>
    /// Every row rounds the amounts in its columns as a tag's window row and the stats beside the graph do (whole megabytes
    /// below a gigabyte, then one decimal): an incident's growth, a release, a tag held, released or growing now, a
    /// process's pool charge and a clear. One column never says "+21.29 GB" on one row and "+21.3 GB" on the next.
    /// </summary>
    [Fact]
    public void EveryRowRoundsItsAmountsAsTheStatsDo()
    {
        long grew = (long)(21.29 * GB), freed = (long)(4.83 * GB);
        Assert.Equal("+21.3 GB", PoolCauses.ForIncident(Incident(IncidentKind.Growth, grew, open: true), "a", Now).Grew);
        var release = PoolCauses.ForIncident(Incident(IncidentKind.Release, -freed, open: false, dropped: freed), "a", Now);
        Assert.Equal(("freed 4.8 GB", "−4.8 GB"), (release.HowMuch, release.Net));
        Assert.Equal("4.8 GB held", PoolCauses.ForTag(Tag(TagState.Retained, freed, 0, 0), "a", null, Now).Grew);
        var released = PoolCauses.ForTag(Tag(TagState.Released, MB, -freed, -freed), "a", null, Now);
        Assert.Equal(("freed 4.8 GB", "−4.8 GB"), (released.HowMuch, released.Net));
        Assert.Equal("+103 MB (now 1.2 GB)", PoolCauses.ForTag(Tag(TagState.Growing, (long)(1.23 * GB), 0, (long)(102.6 * MB)), "a", null, Now).Grew);
        Assert.Equal("+103 MB (now 163 MB)", PoolCauses.ForProcess(Proc(10, 10, (long)(162.6 * MB), 60 * MB), Now).Grew);
        var clear = PoolCauses.ForFinding(new Finding
        {
            TimeUtc = Now.AddMinutes(-5), Kind = FindingKind.CacheClear, Title = "Clear cache (standby list, manual): freed 4.83 GB",
            Data = new Dictionary<string, object?> { ["freedBytes"] = freed, ["error"] = null },
        }, Now);
        Assert.Equal(("freed 4.8 GB", "4.8 GB"), (clear.HowMuch, clear.Cleared));
        // A clear that freed less than half a megabyte freed nothing, in both places.
        var none = PoolCauses.ForFinding(new Finding
        {
            TimeUtc = Now.AddMinutes(-5), Kind = FindingKind.CacheClear, Title = "Clear cache (standby list, manual): freed 100 KB",
            Data = new Dictionary<string, object?> { ["freedBytes"] = 100 * 1024L, ["error"] = null },
        }, Now);
        Assert.Equal(("freed nothing", "nothing"), (none.HowMuch, none.Cleared));
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
    public void NoRowEverShowsANegativeGrowthAndOnlyItsNetChangeIsSigned()
    {
        long[] amounts = [-2 * GB, -300 * MB, -1, 0, 1, 300 * MB, 2 * GB];
        foreach (var a in amounts)
        foreach (var b in amounts)
        {
            foreach (var state in new[] { TagState.Growing, TagState.Retained, TagState.Released })
                AssertOnlyTheNetChangeIsSigned(PoolCauses.ForTag(Tag(state, Math.Abs(a), a, b), "x", null, Now));
            foreach (var kind in Enum.GetValues<IncidentKind>())
                AssertOnlyTheNetChangeIsSigned(PoolCauses.ForIncident(Incident(kind, a, open: b > 0, dropped: Math.Abs(b)), "x", Now));
            // And over a window: whatever the counts, Grew, Cleared and Per hour are never negative.
            var counts = new Core.Growth.GrowthCounts(Math.Abs(a), Math.Abs(b), Math.Abs(a - b) / 2);
            AssertOnlyTheNetChangeIsSigned(PoolCauses.WithWindow(PoolCauses.ForTag(Tag(TagState.Growing, GB, a, b), "x", null, Now), SixHours, counts, GB));
        }
    }

    /// <summary>No cell but Net change holds a sign; Net change is empty, "0", or a "+" or the true minus before an amount.</summary>
    private static void AssertOnlyTheNetChangeIsSigned(CauseRow r)
    {
        var cells = r.Cells;
        Assert.DoesNotMatch(Negative, string.Join(" ", cells.Where((_, i) => i != PoolCauses.NetColumn)));
        Assert.DoesNotMatch(new Regex(@"^\+.*\+|^-|\+-|-\d"), r.Net);
        Assert.Matches(new Regex("^(|0|[+\u2212]\\d.* (B|KB|MB|GB))$"), r.Net);
    }

    // ------------------------------------------------------------------ the window's columns, the clears' rows and the sort

    /// <summary>The Advanced view's default window, measured all through: 6 clears in it.</summary>
    private static readonly Core.Growth.GrowthWindow SixHours = new()
    {
        Span = TimeSpan.FromHours(6), NowUtc = Now, Measured = TimeSpan.FromHours(6), Clears = 6, StartedUtc = Now.AddDays(-1),
        Total = new Core.Growth.GrowthCounts(7578 * MB, 7270 * MB, 0),
    };

    [Fact]
    public void ARowAboutATagTakesItsColumnsFromTheWindowAsTheMockupSaysThem()
    {
        Assert.Equal(["What is growing", "Grew", "Cleared", "Net", "Per hour", "Likely cause", "Confidence", "Impact", "When"], PoolCauses.Columns);
        var growing = PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 100 * MB, 300 * MB), "File system metadata (Reff, refs.sys)", View(Confidence.Correlated), Now);
        var r = PoolCauses.WithWindow(growing, SixHours, new Core.Growth.GrowthCounts(5018 * MB, 4915 * MB, 0), 912 * MB);
        Assert.Equal(["File system metadata (Reff, refs.sys)", "+4.9 GB (now 912 MB)", "4.8 GB", "+103 MB", "836.3 MB/h", "Leaker", "some evidence", "High", "now"], r.Cells);
        Assert.Equal("File system metadata (Reff, refs.sys): +4.9 GB (now 912 MB)", r.Title);
        Assert.True(r.Windowed);
        Assert.Equal((5018 * MB, 4915 * MB, 103 * MB), (r.GrewBytes!.Value, r.ClearedBytes!.Value, r.NetBytes!.Value));
        // Security tokens, which the clears free none of; an incident's row of a counted tag follows the window too.
        var toke = PoolCauses.WithWindow(growing, SixHours, new Core.Growth.GrowthCounts(150 * MB, 0, 0), 760 * MB);
        Assert.Equal(("+150 MB (now 760 MB)", "nothing", "+150 MB", "25 MB/h"), (toke.Grew, toke.Cleared, toke.Net, toke.PerHour));
        var incident = PoolCauses.WithWindow(PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: true), "Open files (File)", Now), SixHours,
            new Core.Growth.GrowthCounts(GB, 0, 0), 2 * GB);
        Assert.Equal(("+1.0 GB (now 2.0 GB)", "nothing", "+1.0 GB"), (incident.Grew, incident.Cleared, incident.Net));
        Assert.Equal("Open files (File): +1.0 GB (now 2.0 GB)", incident.Title);
        // Before half an hour is measured, the rate is left out (the panel says when it shows).
        var early = SixHours with { Measured = TimeSpan.FromMinutes(20) };
        Assert.Equal("", PoolCauses.WithWindow(growing, early, new Core.Growth.GrowthCounts(50 * MB, 0, 0), 912 * MB).PerHour);
        Assert.Null(PoolCauses.WithWindow(growing, early, new Core.Growth.GrowthCounts(50 * MB, 0, 0), 912 * MB).PerHourBytes);
    }

    [Fact]
    public void AClearsRowSaysWhatItFreedInTheClearedColumn()
    {
        Finding Clear(long freed, string? error = null) => new()
        {
            TimeUtc = Now.AddMinutes(-20), Kind = FindingKind.CacheClear, Severity = FindingSeverity.Info, Title = "Clear cache (standby list, automatic): freed 1.2 GB",
            Data = new Dictionary<string, object?> { ["freedBytes"] = freed, ["error"] = error },
        };
        var row = PoolCauses.ForFinding(Clear((long)(1.2 * GB)), Now);
        Assert.Equal("Clear cache (standby list, automatic)", row.What);
        Assert.Equal(("", "1.2 GB", "", ""), (row.Grew, row.Cleared, row.Net, row.PerHour));
        Assert.Equal((long)(1.2 * GB), row.ClearedBytes);
        Assert.Equal("Clear cache (standby list, automatic): freed 1.2 GB", row.Title);
        Assert.Equal("nothing", PoolCauses.ForFinding(Clear(100 * 1024), Now).Cleared);   // under half a megabyte
        Assert.Equal("nothing", PoolCauses.ForFinding(Clear(-50 * MB), Now).Cleared);     // the pool grew in its 2 minutes
        var failed = PoolCauses.ForFinding(Clear(0, "access denied"), Now);
        Assert.Equal(("Clear cache (standby list, automatic) failed", ""), (failed.What, failed.Cleared));
    }

    [Fact]
    public void OnlyRowsAboutATagHappeningNowFollowTheWindow()
    {
        // The same tag in four rows: its open incident and its growing row follow the window; an incident from yesterday
        // and a release keep their own figures (what that incident grew, what the release freed).
        var counts = new Core.Growth.GrowthCounts(5018 * MB, 4915 * MB, 0);
        var window = SixHours with { Tags = new Dictionary<PoolTag, Core.Growth.GrowthCounts> { [PoolTag.Parse("File")] = counts, [PoolTag.Parse("Reff")] = counts } };
        var open = PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: true), "open", Now);
        var closed = PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: false), "closed", Now);
        var growing = PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 100 * MB, 300 * MB), "growing", null, Now);
        var released = PoolCauses.ForTag(Tag(TagState.Released, 100 * MB, -400 * MB, -400 * MB), "released", null, Now);
        var rows = PoolCauses.Windowed([open, closed, growing, released], window, _ => 912 * MB);
        Assert.Equal([true, false, true, false], rows.Select(r => r.Windowed));
        Assert.Equal("+4.9 GB (now 912 MB)", rows[0].Grew);
        Assert.Equal(("+1.0 GB", "2.0 GB/h"), (rows[1].Grew, rows[1].PerHour)); // its own growth over its half hour
        Assert.Equal("+4.9 GB (now 912 MB)", rows[2].Grew);
        Assert.Equal("\u2212400 MB", rows[3].Net);
        Assert.Equal("Grew, Cleared, Net change and Per hour of tags now: last 6 hours", Core.Growth.GrowthText.WindowNote(Core.Growth.StatsWindow.Default));
        Assert.Contains("tags growing or held now and their open incidents", MainForm.WindowNoteTip);
    }

    [Fact]
    public void PerHourSortsEveryKindOfRowByTheRateItShows()
    {
        // An incident's own rate (1 GB in its hour), a tag row with its 10-minute rate (100 MB in 10 minutes: 600 MB/h), a
        // tag the window says grew (836.3 MB/h), and one the window says fell: it shows no rate, so it sorts as none.
        var incident = PoolCauses.ForIncident(Incident(IncidentKind.Growth, GB, open: true), "i", Now);
        var tag = PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 100 * MB, 300 * MB), "t", null, Now);
        var grew = PoolCauses.WithWindow(PoolCauses.ForTag(Tag(TagState.Growing, GB, 10 * MB, 20 * MB), "g", null, Now), SixHours,
            new Core.Growth.GrowthCounts(5018 * MB, 4915 * MB, 0), 912 * MB);
        var fell = PoolCauses.WithWindow(PoolCauses.ForTag(Tag(TagState.Growing, GB, 10 * MB, 20 * MB), "f", null, Now), SixHours,
            Core.Growth.GrowthCounts.Of(-300 * MB, 0), 600 * MB);
        Assert.Equal(("", (double?)null), (fell.PerHour, fell.PerHourBytes));
        List<CauseRow> rows = [fell, grew, tag, incident];
        string Order(bool descending) => string.Join(",", PoolCausesSort.Sort(rows, PoolCauses.PerHourColumn, descending).Select(r => r.What));
        Assert.Equal("t,g,i,f", Order(descending: false)); // 600 MB/h, 836.3 MB/h, 1 GB/h; no rate last
        Assert.Equal("i,g,t,f", Order(descending: true));
    }

    private static CauseRow Row(string what, long? grew, double? perHour = null, Confidence? level = null, string cause = "", int minutesAgo = 0, long? net = null,
        ImpactLevel? impact = null) =>
        new("k:" + what, CauseKind.Tag, what, "", "", cause, "", "") { GrewBytes = grew, PerHourBytes = perHour, Level = level, TimeUtc = Now.AddMinutes(-minutesAgo), NetBytes = net, Impact = impact };

    [Fact]
    public void AColumnSortsByItsNumbersOneWayThenTheOtherWithEmptyCellsLast()
    {
        List<CauseRow> rows = [Row("b", 300 * MB, 50, Confidence.Weak, "Zed", 5, -10, ImpactLevel.Medium), Row("A", null, null, null, "", 1),
            Row("c", 2 * GB, 900, Confidence.ConfirmedByTrace, "alpha", 30, 5, ImpactLevel.High), Row("d", 300 * MB, 10, Confidence.Correlated, "Mid", 10, 0, ImpactLevel.None)];
        string Order(int column, bool descending) => string.Concat(PoolCausesSort.Sort(rows, column, descending).Select(r => r.What));
        Assert.Equal("bdcA", Order(PoolCauses.GrewColumn, false)); // 300 MB, 300 MB (ties keep the list's order), 2 GB; no number last
        Assert.Equal("cbdA", Order(PoolCauses.GrewColumn, true));  // the other way, the empty cell still last
        Assert.Equal("dbcA", Order(PoolCauses.PerHourColumn, false));
        Assert.Equal("bdcA", Order(PoolCauses.NetColumn, false));
        Assert.Equal("Abcd", Order(0, false));   // by name, whatever the case
        Assert.Equal("dcbA", Order(0, true));
        Assert.Equal("cdbA", Order(PoolCauses.CauseColumn, false));      // the likely cause by name; none last
        Assert.Equal("bdcA", Order(PoolCauses.ConfidenceColumn, false)); // the confidence by its strength, not its words
        Assert.Equal("dbcA", Order(PoolCauses.ImpactColumn, false));     // the impact by its level: None, Medium, High; none last
        Assert.Equal("cbdA", Order(PoolCauses.ImpactColumn, true));
        Assert.Equal("cdbA", Order(PoolCauses.WhenColumn, false));       // When: the oldest first
        Assert.Equal("Abdc", Order(PoolCauses.WhenColumn, true));
        Assert.Equal("bAcd", Order(PoolCauses.ClearedColumn, false)); // no row has a number: the list's own order
    }

    // ------------------------------------------------------------------ impact and what PoolWatch already does

    [Fact]
    public void TheImpactColumnIsTheAgreedRuleOfTheGrowthPerHourWithTheClearsAddedBack()
    {
        // Over the window: the tag's growth with PoolWatch's own clears added back, per hour measured (FromGrowth).
        var growing = PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 100 * MB, 300 * MB), "x", null, Now);
        ImpactLevel? Over(long grew, long fell = 0, double hours = 6) =>
            PoolCauses.WithWindow(growing, SixHours with { Measured = TimeSpan.FromHours(hours) }, new Core.Growth.GrowthCounts(grew, grew / 2, fell), GB).Impact;
        Assert.Equal(ImpactLevel.None, Over(6 * 9 * MB));          // under 10 MB an hour
        Assert.Equal(ImpactLevel.Low, Over(6 * 100 * MB));         // 2.3 GB a day
        Assert.Equal(ImpactLevel.Medium, Over(6 * 300 * MB));      // 7 GB a day
        Assert.Equal(ImpactLevel.High, Over(6 * 500 * MB));        // 11.7 GB a day, though the clears keep its net change small
        Assert.Equal(ImpactLevel.None, Over(0, fell: 600 * MB));   // it fell
        Assert.Null(Over(6 * 500 * MB, hours: 0.25));              // not known before half an hour is measured
        Assert.Equal("", PoolCauses.WithWindow(growing, SixHours with { Measured = TimeSpan.FromMinutes(15) }, new Core.Growth.GrowthCounts(GB, 0, 0), GB).Cells[PoolCauses.ImpactColumn]);
        // Each one is the rule's, word for word.
        foreach (var rate in new long[] { 0, 9 * MB, 10 * MB, 210 * MB, 430 * MB, 2 * GB })
            Assert.Equal(ImpactRules.FromGrowth(rate).ToString(),
                PoolCauses.WithWindow(growing, SixHours, new Core.Growth.GrowthCounts(rate * 6, 0, 0), GB).Cells[PoolCauses.ImpactColumn]);
        // A row with no rate of its own has none: a release, a clear, a finding.
        Assert.Null(PoolCauses.ForTag(Tag(TagState.Released, 100 * MB, -400 * MB, -400 * MB), "r", null, Now).Impact);
        Assert.Null(PoolCauses.ForIncident(Incident(IncidentKind.Release, -GB, open: false, dropped: GB), "r", Now).Impact);
        // Nor does a tag whose rate is not measured yet: one that grew in the hour but not in the last 10 minutes. One held is
        // not growing: None, as the agreed rule says (an empty cell means "not known yet" only).
        Assert.Null(PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 0, 300 * MB), "x", null, Now).Impact);
        Assert.Equal(ImpactLevel.None, PoolCauses.ForTag(Tag(TagState.Retained, 912 * MB, 0, 0), "x", null, Now).Impact);
        Assert.Equal("", PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 0, 300 * MB), "x", null, Now).Cells[PoolCauses.ImpactColumn]);
        Assert.Equal(ImpactRules.FromGrowth(600 * MB), PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 100 * MB, 300 * MB), "x", null, Now).Impact);
    }

    [Fact]
    public void TheImpactMeterFillsOnePartPerLevelInTheDesignsColours()
    {
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<ImpactLevel>().Select(ImpactScale.Filled));
        Assert.Equal(new Color?[] { null, UiColors.Low, UiColors.Medium, UiColors.High }, Enum.GetValues<ImpactLevel>().Select(ImpactScale.ColorOf));
        Assert.Equal((Color.FromArgb(212, 167, 44), Color.FromArgb(232, 89, 12), Color.FromArgb(201, 42, 42)), (UiColors.Low, UiColors.Medium, UiColors.High));
        Assert.Equal(ImpactLevel.High, ImpactScale.Parse("High"));
        Assert.Null(ImpactScale.Parse(""));
        Assert.Null(ImpactScale.Parse("7"));
        // Drawn: the filled parts in the level's colour, the rest grey, then the word.
        using var bitmap = new Bitmap(120, 24);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font("Segoe UI", 9f))
            ImpactScale.DrawCell(g, new Rectangle(0, 0, 120, 24), "Medium", font, Color.White, Color.Black);
        var pixels = Enumerable.Range(0, 60).Select(x => bitmap.GetPixel(x, 12).ToArgb()).ToHashSet();
        Assert.Contains(UiColors.Medium.ToArgb(), pixels);
        Assert.Contains(UiColors.Track.ToArgb(), pixels);
        Assert.DoesNotContain(UiColors.High.ToArgb(), pixels);
    }

    /// <summary>
    /// The Impact column as the window opens it, at 100% to 200%: every level's word is drawn whole after the meter. Drawn
    /// in the column and in a wider cell, the column's pixels are the same (no "Med..."), and no ink runs past the column.
    /// At the design's 96 logical px the meter's bars left "Medium" too little room at every scale.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void EveryImpactWordShowsWholeInItsColumn(int percent)
    {
        using var font = new Font("Segoe UI", 9f * percent / 100);
        int column = MainForm.CauseColumnWidths(font, n => n * percent / 100)[PoolCauses.ImpactColumn];
        int height = font.Height * 2, wide = column + 80;
        foreach (var level in Enum.GetValues<ImpactLevel>())
        {
            string word = ImpactScale.Word(level);
            using var inColumn = Cell(new Rectangle(0, 0, column, height));
            using var inWide = Cell(new Rectangle(0, 0, wide, height));
            for (int x = 0; x < wide; x++)
            for (int y = 0; y < height; y++)
            {
                int widePixel = inWide.GetPixel(x, y).ToArgb();
                if (x < column) Assert.True(inColumn.GetPixel(x, y).ToArgb() == widePixel, $"\"{word}\" is cut at {percent}% in {column} px (x {x})");
                else Assert.True(widePixel == Color.White.ToArgb(), $"\"{word}\" runs past the {column} px column at {percent}% (x {x})");
            }

            Bitmap Cell(Rectangle cell)
            {
                var b = new Bitmap(wide, height);
                using var g = Graphics.FromImage(b);
                g.Clear(Color.White);
                ImpactScale.DrawCell(g, cell, word, font, Color.White, Color.Black, highContrast: false);
                return b;
            }
        }
    }

    /// <summary>
    /// High contrast: the meter's filled bars are in the text colour of the row they sit on, as its word is (a selected row's
    /// highlight text on the highlight), not the window's text colour, which can match the highlight.
    /// </summary>
    [Fact]
    public void UnderHighContrastTheMetersBarsAreInTheirRowsTextColour()
    {
        using var font = new Font("Segoe UI", 9f);
        var cell = new Rectangle(0, 0, 140, 24);
        using var bitmap = new Bitmap(cell.Width, cell.Height);
        using (var g = Graphics.FromImage(bitmap))
            ImpactScale.DrawCell(g, cell, "High", font, Color.Cyan, Color.Magenta, highContrast: true);
        var (meter, gap, _) = ImpactScale.CellLayout(cell, font);
        var middles = Enumerable.Range(0, 3)
            .Select(i => bitmap.GetPixel((int)(meter.X + i * (meter.Width + gap) + meter.Width / 2), (int)(meter.Y + meter.Height / 2)).ToArgb()).ToList();
        Assert.Equal(Enumerable.Repeat(Color.Magenta.ToArgb(), 3), middles);

        // The cost meters too: with ink given, their bars take it; without, the window's text colour.
        foreach (var (ink, want) in new (Color?, Color)[] { (Color.Magenta, Color.Magenta), (null, SystemColors.WindowText) })
        {
            using var b = new Bitmap(60, 20);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.Cyan);
                ImpactScale.PaintMeter(g, new RectangleF(2, 8, 10, 4), 2, ImpactLevel.High, highContrast: true, ink);
            }
            Assert.Equal(want.ToArgb(), b.GetPixel(7, 10).ToArgb());
        }
    }

    [Fact]
    public void EveryWindowDrawsTheImpactScaleAndItsColoursFromOnePlace()
    {
        if (SystemInformation.HighContrast) return; // then every window uses the system's colours instead
        // One meter: setup's size (three bars of 14 by 5, 3 apart) and the cost meter's (10 by 4, 2 apart) fill the same bars
        // in the same colours.
        foreach (var (bar, gap) in new[] { (new SizeF(14, 5), 3f), (new SizeF(10, 4), 2f) })
        {
            using var bitmap = new Bitmap(60, 20);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                ImpactScale.PaintMeter(g, new RectangleF(new PointF(2, 8), bar), gap, ImpactLevel.Medium, highContrast: false);
            }
            var middles = Enumerable.Range(0, 3).Select(i => bitmap.GetPixel((int)(2 + i * (bar.Width + gap) + bar.Width / 2), (int)(8 + bar.Height / 2)).ToArgb()).ToList();
            Assert.Equal([UiColors.Medium.ToArgb(), UiColors.Medium.ToArgb(), UiColors.Track.ToArgb()], middles);
        }
        // One palette: setup, the Services and Tasks tabs and Active changes take the design's colours from UiColors.
        Assert.Equal((UiColors.Ink, UiColors.Muted, UiColors.Faint, UiColors.Good, UiColors.Line),
            (PoolWatch.Ui.Setup.SetupTheme.Ink, PoolWatch.Ui.Setup.SetupTheme.Muted, PoolWatch.Ui.Setup.SetupTheme.Faint, PoolWatch.Ui.Setup.SetupTheme.Good,
             PoolWatch.Ui.Setup.SetupTheme.Line));
        Assert.Equal((UiColors.Accent, UiColors.Track, UiColors.Border), (PoolWatch.Ui.Setup.SetupTheme.Accent, PoolWatch.Ui.Setup.SetupTheme.Empty,
            PoolWatch.Ui.Setup.SetupTheme.Border));
        Assert.Equal((UiColors.Ink, UiColors.Muted, UiColors.Faint, UiColors.Good, UiColors.Line), (ListTabStyle.Ink, ListTabStyle.Muted, ListTabStyle.Faint,
            ListTabStyle.Good, ListTabStyle.Line));
        Assert.Equal((UiColors.Ink, UiColors.Muted, UiColors.Faint, UiColors.Good, UiColors.RowLine, UiColors.Warn),
            (ActiveChangesForm.Palette.Ink, ActiveChangesForm.Palette.Muted, ActiveChangesForm.Palette.Faint, ActiveChangesForm.Palette.Good,
             ActiveChangesForm.Palette.RowLine, ActiveChangesForm.Palette.Warn));
    }

    [Fact]
    public void TheDetailsSayWhatPoolWatchAlreadyDoesAboutARow()
    {
        var on = new HandledBy(HandledBy.ScheduleOf(true, 1, 8), GuardWorking: true);
        var off = new HandledBy(HandledBy.ScheduleOf(false, 1, 8), GuardWorking: false);
        var reff = PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 100 * MB, 300 * MB), "File system metadata (Reff, refs.sys)", null, Now);
        Assert.Equal(("Handled: the standby cache is emptied every hour, or at 8 GB.", true), PoolCauses.Handled(reff, on));
        Assert.Equal(("Not handled yet: emptying the standby cache frees it. You can turn on automatic clearing in Settings.", false), PoolCauses.Handled(reff, off));
        // The token leak's security tokens: the guard, and only while Windows' setting is on.
        var toke = reff with { Tag = PoolTag.Parse("Toke") };
        Assert.Equal(("Handled: the token-leak guard is on, so Windows does not leak these.", true), PoolCauses.Handled(toke, on));
        Assert.DoesNotContain("sign out", PoolCauses.Handled(toke, on)!.Value.Text); // the protection does not end there: PoolWatch turns it on again
        Assert.False(PoolCauses.Handled(toke, off)!.Value.Done);
        // A tag the clears do not free, a process and a finding: nothing to say.
        Assert.Null(PoolCauses.Handled(reff with { Tag = PoolTag.Parse("Abcd") }, on));
        Assert.NotNull(PoolCauses.Handled(reff with { Tag = PoolTag.Parse("Abcd"), ClearedBytes = 50 * MB }, on)); // ... unless the clears did free it
        Assert.Null(PoolCauses.Handled(reff with { Kind = CauseKind.Process }, on));
        // The schedule in words, each rule on its own.
        Assert.Equal(new string?[] { "every hour, or at 8 GB", "every 2 hours", "when the kernel pool reaches 7.5 GB", null, null },
            new[] { HandledBy.ScheduleOf(true, 1, 8), HandledBy.ScheduleOf(true, 2, 0), HandledBy.ScheduleOf(true, 0, 7.5), HandledBy.ScheduleOf(true, 0, 0), HandledBy.ScheduleOf(false, 1, 8) });
    }

    [Fact]
    public void TheDetailsSayTheHandledLineUnderTheWordsAsTheDesignHasIt()
    {
        // The title, the plain explanation, then what PoolWatch already does about it; the actions down the right.
        using var form = new Form { Font = new Font("Segoe UI", 9f) };
        var panel = new Panel { Size = new Size(700, 220) };
        form.Controls.Add(panel);
        var title = new WrapLabel { Text = "File system metadata (Reff, refs.sys): +3.9 GB (now 912 MB)" };
        var words = new TextBox { Multiline = true, Text = "ReFS keeps a record for each file while the file sits in the standby cache." };
        var handled = new WrapLabel { Text = "Handled: the standby cache is emptied every hour, or at 8 GB." };
        var actions = new FlowLayoutPanel { Width = 140 };
        MainForm.LayDetails(panel, title, words, handled, actions);
        panel.PerformLayout();
        Assert.True(title.Top == 0 && title.Bottom <= words.Top, $"title {title.Bounds}, words {words.Bounds}");
        Assert.True(words.Bottom <= handled.Top, $"words {words.Bounds}, handled {handled.Bounds}");
        Assert.Equal(panel.ClientSize.Height, handled.Bottom);
        Assert.Equal((panel.ClientSize.Width, 0, panel.ClientSize.Height), (actions.Right, actions.Top, actions.Bottom));
        Assert.True(title.Right <= actions.Left && words.Right <= actions.Left && handled.Right <= actions.Left);
    }
}
