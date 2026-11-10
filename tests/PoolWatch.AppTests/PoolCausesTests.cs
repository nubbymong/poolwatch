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

    /// <summary>
    /// The notice's headline is its title, and the rest its text: Windows 11 shows the notice as a toast with PoolWatch's
    /// name above it, so a title of "PoolWatch" said the name twice and left the headline to the text. A notice that names
    /// no cause says that a click opens PoolWatch (it does: <see cref="TrayAppNoticeTests"/>).
    /// </summary>
    [Fact]
    public void TheTraysIncidentNoticeSaysKernelPoolAndNamesACauseOnlyAtStrongEvidence()
    {
        var info = new TagInfo("<unknown>", "File objects", "pooltag.txt");
        var strong = Open(owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)], suspects: [Process("Leaker.exe")]);
        Assert.Equal(new NoticeText("Kernel pool: Open files (File) is growing", "PoolWatch is keeping an eye on Leaker."), PoolCauses.IncidentNotice(strong, info, Running));

        // A weaker suspect is not named, as on the status line: growth often goes away by itself.
        var weak = Process("Leaker.exe");
        weak.Confidence = Confidence.Correlated;
        var details = new NoticeText("Kernel pool: Open files (File) is growing", "Click here to open PoolWatch for details.");
        Assert.Equal(details, PoolCauses.IncidentNotice(Open(suspects: [weak]), info, Running));
        Assert.Equal(details, PoolCauses.IncidentNotice(Open(), info, Running));
        // A cause nobody knows by a name is not named either.
        var system = Open(owners: [Ranked(new Owner(OwnerKind.System, "system", "the kernel (System worker threads)"), Confidence.StronglyCorrelated)]);
        Assert.Equal(details, PoolCauses.IncidentNotice(system, info, Running));

        var total = PoolCauses.IncidentNotice(Open(IncidentKind.NonPagedTotal, traced: [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:codex", "Codex") }]), info, Running);
        Assert.Equal(new NoticeText("Non-paged pool over the limit", "Kernel pool: Open files (File) is growing. PoolWatch is keeping an eye on Codex."), total);

        // A tag whose words are too long for a title: the title says what happens, and the text what grows.
        var wordy = new TagInfo("examplenetworkdriver.sys", "Receive buffers of the example network", "pooltag.txt");
        var other = new Incident { Id = "20261027-140000-Abcd-64636241", Dir = @"C:\data\incidents\y", Tag = PoolTag.Parse("Abcd"), Kind = IncidentKind.Growth };
        var longOne = PoolCauses.IncidentNotice(other, wordy, Running)!;
        Assert.Equal("Kernel pool growing", longOne.Title);
        Assert.Equal("Kernel pool: Receive buffers of the example network (Abcd, examplenetworkdriver.sys) is growing. Click here to open PoolWatch for details.", longOne.Text);
        foreach (var notice in new[] { PoolCauses.IncidentNotice(strong, info, Running)!, total!, details, longOne })
        {
            Assert.DoesNotMatch(new Regex(@"\.exe\b|\(PID \d+\)|>"), notice.Title + " " + notice.Text); // no exe, no PID, no detector rule
            Assert.True(notice.Text.Length <= 250, notice.Text); // what a notification shows
            Assert.True(notice.Title.Length <= PoolCauses.MaxNoticeTitle, notice.Title);
            Assert.NotEqual("PoolWatch", notice.Title); // the toast names the app already
        }

        // A pool released (a tag that dropped with no growth incident, often after PoolWatch's own clear) is no alarm:
        // no notice at all, however strong its ranking, and so never "is growing" or a cause.
        Assert.Null(PoolCauses.IncidentNotice(Open(IncidentKind.Release, owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]), info, Running));
        Assert.Null(PoolCauses.IncidentNotice(Open(IncidentKind.Release, traced: [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:codex", "Codex") }]), info, Running));
        Assert.Null(PoolCauses.IncidentNotice(Open(IncidentKind.Capture), info, Running));
    }

    /// <summary>
    /// A non-paged pool incident is named one way in the window: its Pool causes row (the details' title), the details
    /// under it and the tray's notice all say "non-paged pool over the limit". Report an issue's digest keeps its own
    /// words ("over its limit"); the Core tests pin those.
    /// </summary>
    [Fact]
    public void TheRowItsDetailsAndTheTraysNoticeNameANonPagedIncidentAlike()
    {
        const string Words = "non-paged pool over the limit";
        var row = PoolCauses.ForIncident(Incident(IncidentKind.NonPagedTotal, 300 * MB, open: true), "Open files (File)", Now);
        Assert.Equal("Open files (File), with " + Words, row.What);
        Assert.StartsWith(row.What + ": ", row.Title);
        // The notice says it as its title, the headline (in capitals: it starts the title).
        Assert.Equal(new NoticeText("Non-paged pool over the limit", "Kernel pool: Open files (File) is growing. Click here to open PoolWatch for details."),
            PoolCauses.IncidentNotice(Open(IncidentKind.NonPagedTotal), new TagInfo("<unknown>", "File objects", "pooltag.txt"), Running));
        Assert.Equal(Words, PoolCauses.NonPagedTitle.ToLowerInvariant());
        Assert.Equal($"h\r\nIncident x ({Words}), tag File\r\n",
            PoolWatch.Core.Util.CopyText.IncidentDetails("""{ "id": "x", "kind": "NonPagedTotal", "tag": "File", "headline": "h" }"""));
    }

    [Fact]
    public void TheTrayWordsAnOpenedIncidentWithTheTagsDescriptionAndLogsAFailure()
    {
        var info = new TagInfo("<unknown>", "File objects", "pooltag.txt");
        var log = new List<string>();
        var asked = new List<PoolTag>();
        var strong = Open(owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]);
        Assert.Equal(new NoticeText("Kernel pool: Open files (File) is growing", "PoolWatch is keeping an eye on Leaker."),
            PoolCauses.TrayNotice(strong, PoolNoticeLevel.EveryGrowth, t => { asked.Add(t); return info; }, Running, log.Add)); // never the bare tag code
        Assert.Equal([PoolTag.Parse("File")], asked);
        Assert.Null(PoolCauses.TrayNotice(Open(IncidentKind.Release, owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]), PoolNoticeLevel.EveryGrowth, _ => info, Running, log.Add));
        Assert.Empty(log);
        Assert.Null(PoolCauses.TrayNotice(strong, PoolNoticeLevel.EveryGrowth, _ => throw new InvalidOperationException("no knowledge"), Running, log.Add));
        Assert.Equal(["incident notice: no knowledge"], log);
    }

    /// <summary>
    /// Settings' Notifications choice decides which kernel pool notices the tray shows, as the incident is when it opens:
    /// every one (the default); only those the tray icon shows red, by the icon's own rule (the non-paged pool over its
    /// limit and still rising, or a growth whose cause a trace confirmed), so a growth with strong evidence, which is
    /// amber, shows none; or none. One held back is a line in the log, with its title. A release never has a notice.
    /// </summary>
    [Fact]
    public void EachNotificationsChoiceShowsTheKernelPoolNoticesItSays()
    {
        var info = new TagInfo("<unknown>", "File objects", "pooltag.txt");
        var growth = Open();
        var strong = Open(owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]);
        var traced = Open(traced: [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:codex", "Codex") }]);
        var nonPaged = Open(IncidentKind.NonPagedTotal);
        var release = Open(IncidentKind.Release, owners: [Ranked(LeakerByPid, Confidence.StronglyCorrelated)]);
        var log = new List<string>();
        bool Shown(Incident i, PoolNoticeLevel level) => PoolCauses.TrayNotice(i, level, _ => info, Running, log.Add) is not null;

        Assert.All(new[] { growth, strong, traced, nonPaged }, i => Assert.True(Shown(i, PoolNoticeLevel.EveryGrowth), i.Kind.ToString()));
        Assert.Empty(log);
        Assert.Equal([false, false, true, true], new[] { growth, strong, traced, nonPaged }.Select(i => Shown(i, PoolNoticeLevel.NeedsAttention)));
        Assert.Equal(["notice not shown (NeedsAttention): Kernel pool: Open files (File) is growing",
                      "notice not shown (NeedsAttention): Kernel pool: Open files (File) is growing"], log);
        log.Clear();
        Assert.All(new[] { growth, strong, traced, nonPaged }, i => Assert.False(Shown(i, PoolNoticeLevel.Off), i.Kind.ToString()));
        Assert.Equal(4, log.Count);
        Assert.Contains("notice not shown (Off): Non-paged pool over the limit", log);
        log.Clear();
        foreach (var level in Enum.GetValues<PoolNoticeLevel>()) Assert.False(Shown(release, level));
        Assert.Empty(log); // a release is no notice to hold back

        // The red rule is the tray icon's own: what it shows red, and nothing else.
        foreach (var kind in Enum.GetValues<IncidentKind>())
            foreach (Confidence? cause in Enum.GetValues<Confidence>().Cast<Confidence?>().Append(null))
                Assert.Equal(UiData.State([(kind, cause)], retained: false) == TrayState.Red, PoolCauses.NoticeWanted(PoolNoticeLevel.NeedsAttention, kind, cause));
        Assert.True(PoolCauses.NoticeWanted(PoolNoticeLevel.EveryGrowth, IncidentKind.Growth, null));
        Assert.False(PoolCauses.NoticeWanted(PoolNoticeLevel.Off, IncidentKind.NonPagedTotal, Confidence.ConfirmedByTrace));
    }

    /// <summary>
    /// "Only when it needs attention" tells of a growth once it turns red, not only as it opens: a new growth incident has
    /// no trace when it opens (the incident manager raises its event before any capture), so a choice judged only then
    /// would never tell of the growth a trace confirms later, which turns the tray icon red. Its notice comes at the tray's
    /// next refresh, once for the episode, naming the cause the trace confirmed; the non-paged pool's comes as it opens and
    /// not again; a re-opened incident is a new episode. Every growth tells as it opens and adds nothing later, Off tells
    /// nothing, and a change of the choice repeats no notice. Closed incidents are forgotten, and a notice that cannot be
    /// worded is logged once, not at every refresh.
    /// </summary>
    [Fact]
    public void OnlyWhenItNeedsAttentionTellsOfAGrowthOnceItTurnsRedAndOncePerEpisode()
    {
        var info = new TagInfo("<unknown>", "File objects", "pooltag.txt");
        var log = new List<string>();
        var notices = new IncidentNotices(_ => info, () => Running, log.Add);
        const PoolNoticeLevel attention = PoolNoticeLevel.NeedsAttention;
        static Incident New(string id, IncidentKind kind = IncidentKind.Growth) =>
            new() { Id = id, Dir = @"C:\data\incidents\" + id, Tag = PoolTag.Parse("File"), Kind = kind };
        static List<TraceOwner> Codex() => [new TraceOwner { Owner = new Owner(OwnerKind.App, "app:codex", "Codex") }];

        // A growth opens amber (no cause yet): no notice, then or at a refresh, nor with strong evidence (still amber).
        var growth = New("growth");
        var open = new List<Incident> { growth };
        Assert.Null(notices.Opened(growth, attention));
        Assert.Equal(["notice not shown (NeedsAttention): Kernel pool: Open files (File) is growing"], log);
        Assert.Empty(notices.TurnedRed(open, attention));
        growth.Owners = [Ranked(LeakerByPid, Confidence.StronglyCorrelated)];
        Assert.Empty(notices.TurnedRed(open, attention));
        // A trace confirms its cause: the icon turns red, and its notice comes at the next refresh, once.
        growth.TraceOwners = Codex();
        Assert.Equal(new[] { new NoticeText("Kernel pool: Open files (File) is growing", "PoolWatch is keeping an eye on Codex.") }, notices.TurnedRed(open, attention));
        Assert.Empty(notices.TurnedRed(open, attention));
        Assert.Empty(notices.TurnedRed(open, attention));
        // The non-paged pool over its limit is red as it opens: its notice then, and none at a refresh.
        var nonPaged = New("nonpaged", IncidentKind.NonPagedTotal);
        open.Add(nonPaged);
        Assert.Equal(PoolCauses.NonPagedTitle, notices.Opened(nonPaged, attention)?.Title);
        Assert.Empty(notices.TurnedRed(open, attention));
        // The growth re-opens (a new episode, its trace still on it): red as it opens, told then, and not again.
        growth.Reopened++;
        Assert.NotNull(notices.Opened(growth, attention));
        Assert.Empty(notices.TurnedRed(open, attention));
        Assert.Single(log); // nothing more was held back or failed
        // Closed incidents are forgotten; open ones whose notice was shown are kept.
        Assert.Equal(2, notices.Held);
        open.Remove(growth);
        Assert.Empty(notices.TurnedRed(open, attention));
        Assert.Equal(1, notices.Held);
        open.Clear();
        Assert.Empty(notices.TurnedRed(open, PoolNoticeLevel.EveryGrowth)); // forgotten under any choice
        Assert.Equal(0, notices.Held);

        // Every growth: told as it opens, nothing added when it turns red, nor after a change to "only when it needs attention".
        var every = New("every");
        Assert.NotNull(notices.Opened(every, PoolNoticeLevel.EveryGrowth));
        every.TraceOwners = Codex();
        Assert.Empty(notices.TurnedRed([every], PoolNoticeLevel.EveryGrowth));
        Assert.Empty(notices.TurnedRed([every], attention));
        // Off: nothing as it opens or when it turns red; changed to "only when it needs attention" while it is red, told
        // once then (nothing was told before).
        var off = New("off");
        Assert.Null(notices.Opened(off, PoolNoticeLevel.Off));
        off.TraceOwners = Codex();
        Assert.Empty(notices.TurnedRed([off], PoolNoticeLevel.Off));
        Assert.Single(notices.TurnedRed([off], attention));
        Assert.Empty(notices.TurnedRed([off], attention));

        // A notice that cannot be worded: logged once, never thrown, and not tried at every refresh.
        log.Clear();
        var failing = new IncidentNotices(_ => throw new InvalidOperationException("no knowledge"), () => Running, log.Add);
        var traced = New("traced");
        traced.TraceOwners = Codex();
        Assert.Empty(failing.TurnedRed([traced], attention));
        Assert.Empty(failing.TurnedRed([traced], attention));
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
        Assert.True(PoolCauses.Shows(F(FindingKind.CacheClear, FindingSeverity.Info, hour)));
        Assert.True(PoolCauses.Shows(F(FindingKind.SpawnStorm, FindingSeverity.Alert, hour)));
        Assert.True(PoolCauses.Shows(F(FindingKind.ChildAccumulation, FindingSeverity.Warning, hour)));
        Assert.False(PoolCauses.Shows(F(FindingKind.SpawnStorm, FindingSeverity.Info, hour)));   // a storm that did not open
        Assert.False(PoolCauses.Shows(F(FindingKind.Lag, FindingSeverity.Alert, hour)));         // not a pool cause
        Assert.False(PoolCauses.Shows(F(FindingKind.Mark, FindingSeverity.Info, hour)));
        Assert.False(PoolCauses.Shows(F(FindingKind.VersionChange, FindingSeverity.Warning, hour)));
        // How old a listed finding may be is the log's to say (FindingLog.Within, a day on the clock that only goes forward).
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

    /// <summary>
    /// The details as the window builds them (<see cref="MainForm.LayDetails"/>, <see cref="MainForm.DetailActions"/>), at
    /// <paramref name="percent"/> as far as a test can scale them (their text and their sizes): the split's lower panel,
    /// padded as the window pads it, with the title, the words, the green line (hidden for no text, as
    /// <c>MainForm.ShowHandled</c> hides it) and the three actions all showing.
    /// </summary>
    private sealed class Details : IDisposable
    {
        public readonly Form Form;
        public readonly Panel Panel;
        public readonly WrapLabel Title, Handled;
        public readonly TextBox Words;
        public readonly Button[] Buttons;
        public readonly FlowLayoutPanel Actions;
        public readonly DetailsPane Pane;
        public readonly Func<int, int> P;

        public Details(int percent, string title, string handled, string words = "Last 6 hours (1 min measured): it grew 1 MB and it is 1 MB higher.")
        {
            P = n => n * percent / 100;
            Form = new Form { Font = new Font("Segoe UI", 9f * percent / 100) };
            Panel = new Panel { Padding = new Padding(P(4), 0, P(4), P(4)) };
            Title = new WrapLabel { Font = new Font("Segoe UI Semibold", 10f * percent / 100), Padding = new Padding(P(4), P(8), P(4), P(2)), Text = title };
            Handled = new WrapLabel { Font = new Font("Segoe UI", 9f * percent / 100), Padding = new Padding(P(4), P(6), P(4), P(4)), Text = handled, Visible = handled.Length > 0 };
            Words = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f * percent / 100), Text = words };
            Buttons = [new() { Text = "Open report folder", AutoSize = true }, new() { Text = "Copy summary", AutoSize = true }, new() { Text = "Export report...", AutoSize = true }];
            Actions = MainForm.DetailActions(P, Buttons);
            Pane = MainForm.LayDetails(Panel, Title, Words, Handled, Actions);
            Form.Controls.Add(Panel);
            CreateHandles(Form); // native windows, never shown
        }

        /// <summary>
        /// Created as a shown window's controls are, still never shown: WinForms sets a scroll position
        /// (<see cref="ScrollableControl.AutoScrollPosition"/>) only on a created control.
        /// </summary>
        public void Create() => CreateControl.Invoke(Form, [true]);

        /// <summary>WinForms' own Control.CreateControl(bool ignoreVisible), found by name: a WinForms without it fails here, by name.</summary>
        private static readonly System.Reflection.MethodInfo CreateControl = typeof(Control).GetMethod("CreateControl",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, [typeof(bool)], null) ?? throw new MissingMethodException("Control", "CreateControl(bool)");

        /// <summary>
        /// A row's details shown as <c>MainForm.ShowCauseDetails</c> shows them: the row's key first
        /// (<see cref="DetailsPane.ShowRow"/>), then its green line, title and actions.
        /// </summary>
        public void Show(string key, string title, string handled, bool folder, bool summary)
        {
            Pane.ShowRow(key);
            Handled.Text = handled;
            Handled.Visible = handled.Length > 0;
            Title.Text = title;
            ShowActions(folder, summary);
        }

        /// <summary>
        /// "Open report folder" for a row with a report folder, "Copy summary" for one with a summary, and "Export
        /// report..." always. A shown window lays the actions' stack out when one shows or hides; one never shown does not,
        /// so it is done here.
        /// </summary>
        public void ShowActions(bool folder, bool summary)
        {
            (Buttons[0].Visible, Buttons[1].Visible) = (folder, summary);
            Actions.PerformLayout();
        }

        /// <summary>The actions shown, in words.</summary>
        public string ActionsShown => string.Join(", ", Buttons.Where(WindowFitTests.OwnVisible).Select(b => b.Text));

        /// <summary>Everything cut off or outside its parent in the details (none is shown, so each control's own Visible counts).</summary>
        public List<string> Clipped()
        {
            var problems = new List<string>();
            MainForm.ClippedControls(Panel, problems, WindowFitTests.OwnVisible);
            return problems;
        }

        public void Dispose() => Form.Dispose();
    }

    /// <summary>The longest title the details show: the documented ReFS row (<see cref="CauseRow.Title"/>).</summary>
    internal const string LongTitle = "File system metadata (Reff, refs.sys): +4.9 GB (now 912 MB)";

    /// <summary>A long explanation, as an incident's details read (the words scroll inside their own box).</summary>
    internal const string LongWords = "Last 6 hours (1 min measured): it grew 4.9 GB and it is 4.9 GB higher.\r\n\r\nReFS keeps a record for each file " +
        "while the file sits in the standby cache. On a Dev Drive with plenty of memory Windows rarely empties that cache, so the records add up " +
        "as tools touch thousands of files an hour. Emptying the standby cache frees them.";

    /// <summary>
    /// The longest green lines, as <see cref="PoolCauses.Handled"/> words them: both "Not handled yet" lines (automatic
    /// clearing off; the token-leak guard off) and the token-leak guard's "Handled" line.
    /// </summary>
    internal static string[] LongGreenLines()
    {
        var on = new HandledBy(HandledBy.ScheduleOf(true, 1, 8), GuardWorking: true);
        var off = new HandledBy(HandledBy.ScheduleOf(false, 1, 8), GuardWorking: false);
        var reff = PoolCauses.ForTag(Tag(TagState.Growing, 912 * MB, 100 * MB, 300 * MB), "File system metadata (Reff, refs.sys)", null, Now);
        var toke = reff with { Tag = PoolTag.Parse("Toke") };
        return [PoolCauses.Handled(reff, off)!.Value.Text, PoolCauses.Handled(toke, off)!.Value.Text, PoolCauses.Handled(toke, on)!.Value.Text];
    }

    /// <summary>
    /// The actions stand in one column, 8 px in and down and 6 px apart, whatever room they are given: where the details
    /// are shorter than the column, the details scroll (<see cref="DetailsPane"/>). The round before, a stack too short
    /// for its column wrapped the actions into columns side by side, which narrowed the title and the green line until the
    /// green line lay over the title.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void TheActionsStandInOneColumnWhateverRoomTheyHave(int percent)
    {
        int P(int n) => n * percent / 100;
        using var form = new Form { Font = new Font("Segoe UI", 9f * percent / 100) };
        Button[] buttons = [new() { Text = "Open report folder", AutoSize = true }, new() { Text = "Copy summary", AutoSize = true }, new() { Text = "Export report...", AutoSize = true }];
        var actions = MainForm.DetailActions(P, buttons);
        var panel = new Panel { Size = new Size(P(696), P(170)) };
        panel.Controls.Add(actions);
        form.Controls.Add(panel);
        CreateHandles(form);
        foreach (int height in new[] { P(170), P(110), P(66), P(40), P(20) })
        {
            panel.Height = height;
            for (int i = 0; i < buttons.Length; i++)
                Assert.Equal(new Point(P(8), i == 0 ? P(8) : buttons[i - 1].Bottom + P(6)), buttons[i].Location);
            Assert.Equal(P(8) + buttons.Max(b => b.Width) + P(4), actions.Width);
        }
    }

    /// <summary>
    /// What is wrong with the details as they are laid out now (empty: nothing). In order, from the top: the title, the
    /// words and the green line one under the other, none over another, each shown text with a height (the title and the
    /// green line their whole height, the words at least two of their lines), all beside the actions, which stand in one
    /// column (8 px in and down, 6 px apart). The
    /// details lie on a content that scrolls up and down in the pane, as wide as the pane's room (never sideways), and
    /// tall enough for all of it: the scroll range covers the content, and each shown action, brought into view as the
    /// focus brings it, lies whole inside the pane. The content is where the pane is scrolled to, with no gap under it,
    /// and the scroll bar shows only where something would be hidden without it: measured here from what the details
    /// hold, at the pane's whole width (the title, two lines of the words, the green line, the last shown action's
    /// bottom). Nothing is cut off sideways or outside its parent (ClippedControls).
    /// </summary>
    internal static List<string> DetailsProblems(Control panel, WrapLabel title, Control words, WrapLabel handled, Control actions, IReadOnlyList<Button> buttons, Func<int, int> p)
    {
        var problems = new List<string>();
        var content = title.Parent!;
        var shown = buttons.Where(WindowFitTests.OwnVisible).ToList();
        bool green = WindowFitTests.OwnVisible(handled);
        // From the top: the title, the words, the green line; nothing over anything.
        if (title.Top < 0) problems.Add($"the title starts above the details ({title.Bounds})");
        if (title.Bottom > words.Top) problems.Add($"the title ({title.Bounds}) runs over the words ({words.Bounds})");
        if (green && words.Bottom > handled.Top) problems.Add($"the green line ({handled.Bounds}) lies over the words ({words.Bounds})");
        if (green && title.Bottom > handled.Top) problems.Add($"the green line ({handled.Bounds}) lies over the title ({title.Bounds})");
        if ((green ? handled : words).Bottom > content.ClientSize.Height) problems.Add($"the details run past their content's bottom ({content.ClientSize.Height})");
        // Each shown text has a height: the title and the green line their whole one.
        if (title.Height <= 0 || title.Height != title.HeightAt(title.Width)) problems.Add($"the title is {title.Height} px tall, needs {title.HeightAt(title.Width)}");
        if (words.Height < 2 * words.Font.Height) problems.Add($"the words are {words.Height} px tall, less than two of their lines ({2 * words.Font.Height} px)");
        if (green && (handled.Height <= 0 || handled.Height != handled.HeightAt(handled.Width))) problems.Add($"the green line is {handled.Height} px tall, needs {handled.HeightAt(handled.Width)}");
        // Beside the actions, which stand in one column at the right.
        foreach (var c in new Control[] { title, words, handled })
            if (c.Right > actions.Left) problems.Add($"{c.GetType().Name} ({c.Bounds}) runs under the actions ({actions.Bounds})");
        if (actions.Right != content.ClientSize.Width) problems.Add($"the actions ({actions.Bounds}) do not end at the content's right ({content.ClientSize.Width})");
        for (int i = 0; i < shown.Count; i++)
        {
            var want = new Point(p(8), i == 0 ? p(8) : shown[i - 1].Bottom + p(6));
            if (shown[i].Location != want) problems.Add($"\"{shown[i].Text}\" is at {shown[i].Location}, not at {want} in one column");
            if (shown[i].Bottom > actions.ClientSize.Height) problems.Add($"\"{shown[i].Text}\" ({shown[i].Bounds}) lies below the actions' column ({actions.Size})");
        }
        // On a content that scrolls up and down, whole, in the pane's room.
        var pane = content.Parent;
        for (; pane is not null and not ScrollableControl { AutoScroll: true }; pane = pane.Parent) { }
        if (pane is not ScrollableControl scroll)
        {
            problems.Add("the details do not scroll: nothing above them scrolls");
            return problems;
        }
        if (content.Parent != scroll) problems.Add("the details' content is not the scrolling pane's own child");
        if (content.Left != 0 || content.Top != scroll.AutoScrollPosition.Y) problems.Add($"the content ({content.Bounds}) is not where the pane is scrolled to ({scroll.AutoScrollPosition})");
        if (content.Bottom < scroll.ClientSize.Height) problems.Add($"a gap under the content ({content.Bounds}) in the pane ({scroll.ClientSize})");
        if (content.Width > scroll.ClientSize.Width || scroll.HorizontalScroll.Visible) problems.Add($"the content ({content.Width} px) is wider than the pane's room ({scroll.ClientSize.Width} px)");
        // The scroll bar shows exactly when the content is taller than the pane, and reaches all of it: its range is the
        // content's height, a page of it the pane's (WinForms: the largest position is Maximum - LargeChange + 1).
        var bar = scroll.VerticalScroll;
        if (content.Height > scroll.ClientSize.Height != bar.Visible) problems.Add($"the content is {content.Height} px in a pane of {scroll.ClientSize.Height}, scroll bar shown: {bar.Visible}");
        int reach = bar.Visible ? bar.Maximum + 1 : scroll.ClientSize.Height;
        if (bar.Visible && (reach < content.Height || bar.LargeChange != scroll.ClientSize.Height || scroll.DisplayRectangle.Height < content.Height))
            problems.Add($"the scroll bar reaches {reach} px a page of {bar.LargeChange} at a time (pane {scroll.ClientSize.Height}, range {scroll.DisplayRectangle.Height}); the content is {content.Height}");
        // ... and shows only where something would be hidden without it. At the pane's whole width (no bar beside the
        // texts) the details need the title, two lines of the words and the green line, or the last shown action's bottom
        // (not the blank margin under it), whichever ends lower.
        int room = scroll.Width - scroll.Padding.Horizontal, text = room - content.Padding.Horizontal - actions.Width;
        int texts = content.Padding.Top + title.HeightAt(text) + 2 * words.Font.Height + (green ? handled.HeightAt(text) : 0);
        int lastAction = shown.Count > 0 ? actions.Top + shown[^1].Bottom : 0;
        int fits = Math.Max(texts, lastAction) + content.Padding.Bottom;
        if (bar.Visible && fits <= scroll.ClientSize.Height)
            problems.Add($"a scroll bar shows with nothing to hide: the details need {fits} px (texts {texts}, last action {lastAction}) of the pane's {scroll.ClientSize.Height}; the content is {content.Height}");
        foreach (var b in shown)
        {
            // Each action lies within what the scroll bar reaches, and shows whole once scrolled to wherever the pane is at
            // least its height (WinForms scrolls the control that takes the focus into view).
            var on = new Rectangle(actions.Left + b.Left, actions.Top + b.Top, b.Width, b.Height);
            if (on.Top < 0 || on.Bottom > reach) problems.Add($"\"{b.Text}\" ({on}) lies beyond the scroll bar's reach ({reach} px)");
            if (on.Right > scroll.ClientSize.Width) problems.Add($"\"{b.Text}\" ({on}) runs past the pane's right ({scroll.ClientSize.Width} px)");
        }
        MainForm.ClippedControls(panel, problems, WindowFitTests.OwnVisible);
        return problems;
    }

    /// <summary>Every control's native window under <paramref name="root"/>, as showing it creates them (the scroll bars' numbers follow only then).</summary>
    internal static void CreateHandles(Control root)
    {
        _ = root.Handle;
        foreach (Control c in root.Controls) CreateHandles(c);
    }

    /// <summary>
    /// The details at every height, 2 px at a time, from less than the window's smallest size gives them to more than its
    /// own size, at the widths a 760 px (the smallest) and a 1024 px window give them, at 100% and 150%, with the longest
    /// title, each long green line and none, and with each set of actions a row can have
    /// (<c>MainForm.ShowCauseDetails</c>): all three (an incident, or a finding, with its report folder); Copy summary and
    /// Export (a ranked pool tag, or a report whose folder is gone); Export alone (a process, or a pool tag not ranked
    /// yet). A report folder never comes without Copy summary: only incidents and findings have one, and Copy summary
    /// always has something to copy for both (an incident's report summary, a finding's evidence). The title, the words
    /// and the green line one under the other, never over each other; every action in its one column; where the details
    /// are shorter than that, they scroll to all of it, and only then. At the smallest window a CI ui run laid the details
    /// out 66 px tall: one column cut its actions off, and three columns side by side (the round before) left the title
    /// and the green line so little width that the green line lay over the title.
    /// </summary>
    [Theory]
    [InlineData(100, 760)]
    [InlineData(100, 1024)]
    [InlineData(150, 760)]
    [InlineData(150, 1024)]
    public void TheDetailsNeverOverlapAndScrollToEveryActionAtEveryHeight(int percent, int windowWidth)
    {
        int scrolled = 0, heights = 0;
        foreach (string green in LongGreenLines().Append(""))
            foreach (var (folder, summary) in new[] { (true, true), (false, true), (false, false) })
            {
                using var d = new Details(percent, LongTitle, green, LongWords);
                d.ShowActions(folder, summary);
                var P = d.P;
                for (int logical = 20; logical <= 170; logical += 2)
                {
                    d.Panel.Size = new Size(P(windowWidth - 64), P(logical)); // the details' width in a window that wide (696 px at 760)
                    var problems = DetailsProblems(d.Panel, d.Title, d.Words, d.Handled, d.Actions, d.Buttons, P);
                    Assert.True(problems.Count == 0, $"details {d.Panel.Size} at {percent}%, actions {d.ActionsShown}, \"{green}\":{Environment.NewLine}" +
                                                     $"title {d.Title.Bounds}, words {d.Words.Bounds}, green line {d.Handled.Bounds}, actions {d.Actions.Bounds}, content {d.Title.Parent!.Bounds}" +
                                                     Environment.NewLine + string.Join(Environment.NewLine, problems));
                    heights++;
                    if (d.Title.Parent!.Height > d.Title.Parent!.Parent!.ClientSize.Height) scrolled++;
                }
            }
        Assert.True(scrolled > 0 && scrolled < heights, $"the details scrolled at {scrolled} of {heights} heights: the sweep must reach both");
    }

    /// <summary>
    /// Another row selected, with the details' size unchanged: its longer title and green line, or its actions shown or
    /// hidden, lay the details out again, so the content grows to what they need (and scrolls) or shrinks back to the
    /// pane's height. Laid out only when the details were resized, they kept the old row's height: the green line lay
    /// over the words, or the third action below the content.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void AnotherRowsTextsAndActionsLayTheDetailsOutAgain(int percent)
    {
        using var d = new Details(percent, "Pool tag Ntfc (ntfs.sys): +1 MB (now 5 MB)", "Handled: the standby cache is emptied every hour, or at 8 GB.");
        var P = d.P;
        var content = d.Title.Parent!;
        void Check(string row)
        {
            var problems = DetailsProblems(d.Panel, d.Title, d.Words, d.Handled, d.Actions, d.Buttons, P);
            Assert.True(problems.Count == 0, $"{row} at {d.Panel.Size}, {percent}%: title {d.Title.Bounds}, words {d.Words.Bounds}, green line {d.Handled.Bounds}, " +
                                             $"actions {d.Actions.Bounds}, content {content.Bounds}{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
        }
        d.ShowActions(folder: false, summary: false);
        d.Panel.Size = new Size(P(960), P(60)); // a 1024 px window's details, short
        Check("a row with one action");
        int one = content.Height;
        d.ShowActions(folder: true, summary: true);
        Check("a row with three actions");
        Assert.True(content.Height > one, $"the content was {one} px for one action and is {content.Height} px for three");
        d.Panel.Size = new Size(P(696), P(60)); // the smallest window's
        int shortRow = content.Height;
        d.Title.Text = LongTitle;
        d.Handled.Text = LongGreenLines()[0];
        d.ShowActions(folder: false, summary: false);
        Check("a row with the long texts and one action");
        Assert.NotEqual(shortRow, content.Height);
        d.Panel.Size = new Size(P(696), P(240)); // taller than the details need
        d.Title.Text = "Pool tag Ntfc (ntfs.sys): +1 MB (now 5 MB)";
        Check("a short row in tall details");
        Assert.Equal(d.Title.Parent!.Parent!.ClientSize.Height, content.Height); // no taller than the pane: nothing to scroll
    }

    /// <summary>
    /// A row's key as a sample builds it: a new string each time ("inc:" + the report folder's name), equal to the last
    /// one but never the same object, so the details tell the same row by its key's value, not by which string it is.
    /// </summary>
    private static string Fresh(string key)
    {
        string fresh = new(key.AsSpan());
        Assert.False(ReferenceEquals(key, fresh));
        return fresh;
    }

    /// <summary>
    /// Another row selected opens its details at their top, the title whole in view, as its words open
    /// (<c>MainForm.SetText</c>); the same row refreshed by a sample, with its key built again, stays where it was
    /// scrolled. On a created pane at the smallest window's details, scrolled to the last action as the Tab key brings it
    /// into view. Kept for every row, a row selected after that opened part-way down with its title out of view, and the
    /// keyboard could not bring it back.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void AnotherRowsDetailsOpenAtTheirTopAndTheSameRowKeepsItsScroll(int percent)
    {
        using var d = new Details(percent, LongTitle, LongGreenLines()[0], LongWords);
        d.Create();
        var P = d.P;
        var content = d.Pane.Content;
        string At() => $"at {percent}%: scrolled to {d.Pane.AutoScrollPosition}, content {content.Bounds} in {d.Pane.ClientSize}, title {d.Title.Bounds}";
        d.Panel.Size = new Size(P(696), P(48)); // the smallest window's details (760 x 540)
        d.Show("inc:a", LongTitle, LongGreenLines()[0], folder: true, summary: true);
        d.Pane.ScrollControlIntoView(d.Buttons[2]);
        int scrolled = d.Pane.AutoScrollPosition.Y;
        Assert.True(scrolled < 0 && content.Top + d.Title.Bottom <= 0, "scrolled to the last action, the title is out of view " + At());
        // The same row refreshed by a sample (its title's numbers move): where it was scrolled.
        d.Show(Fresh("inc:a"), LongTitle.Replace("+4.9 GB", "+5.0 GB"), LongGreenLines()[0], folder: true, summary: true);
        Assert.True((d.Pane.AutoScrollPosition.Y, content.Top) == (scrolled, scrolled), "the same row's refresh moved its details " + At());
        // Another row: at the top, its title whole in view.
        d.Show("tag:Toke", "Pool tag Toke (nt): +1 MB (now 5 MB)", LongGreenLines()[2], folder: false, summary: true);
        Assert.True((d.Pane.AutoScrollPosition.Y, content.Top, d.Title.Top) == (0, 0, 0) && d.Title.Bottom <= d.Pane.ClientSize.Height, "another row's details open part-way down " + At());
        var problems = DetailsProblems(d.Panel, d.Title, d.Words, d.Handled, d.Actions, d.Buttons, P);
        Assert.True(problems.Count == 0, At() + Environment.NewLine + string.Join(Environment.NewLine, problems));
        // Scrolled again, the first row selected again opens at its top too.
        d.Pane.ScrollControlIntoView(d.Buttons[2]);
        Assert.True(d.Pane.AutoScrollPosition.Y < 0, "the second row's details did not scroll " + At());
        d.Show("inc:a", LongTitle, LongGreenLines()[0], folder: true, summary: true);
        Assert.True((d.Pane.AutoScrollPosition.Y, content.Top) == (0, 0), "the first row, selected again, opens part-way down " + At());
    }

    /// <summary>
    /// Scrolled details stay laid out where they are scrolled to. On a created pane scrolled to its last action (as the
    /// Tab key brings it into view): the same row refreshed with a shorter green line and its report folder gone, then the
    /// window resized taller, shorter, wider and narrower, scrolled to the last action again before each. Each time the
    /// content's top is the pane's scroll position, with no gap under it and no sideways scroll bar. Placed at the pane's
    /// top whatever the scroll, the content jumped up while the scroll bar stayed, and after a resize lay part-way down,
    /// wider than the pane, with a sideways bar.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void ScrolledDetailsStayWhereTheyAreScrolledThroughRefreshesAndResizes(int percent)
    {
        using var d = new Details(percent, LongTitle, LongGreenLines()[0], LongWords);
        d.Create();
        var P = d.P;
        var content = d.Pane.Content;
        void Check(string step)
        {
            var problems = DetailsProblems(d.Panel, d.Title, d.Words, d.Handled, d.Actions, d.Buttons, P);
            Assert.True(problems.Count == 0, $"{step}, details {d.Panel.Size} at {percent}%: scrolled to {d.Pane.AutoScrollPosition}, content {content.Bounds} in {d.Pane.ClientSize}" +
                                             Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
        d.Panel.Size = new Size(P(696), P(48)); // the smallest window's details (760 x 540)
        d.Show("inc:a", LongTitle, LongGreenLines()[0], folder: true, summary: true);
        d.Pane.ScrollControlIntoView(d.Buttons[2]);
        Assert.True(d.Pane.AutoScrollPosition.Y < 0, "the details did not scroll");
        Check("scrolled to the last action");
        d.Show(Fresh("inc:a"), LongTitle, LongGreenLines()[2], folder: false, summary: true);
        Assert.True(d.Pane.AutoScrollPosition.Y < 0, "the same row's refresh took the details back to their top");
        Check("the same row refreshed, with a shorter green line and its report folder gone");
        foreach (var (width, height) in new[] { (696, 60), (696, 40), (960, 48), (1216, 120), (696, 48), (960, 300), (696, 48) })
        {
            d.Pane.ScrollControlIntoView(d.Buttons[2]);
            d.Panel.Size = new Size(P(width), P(height));
            Check("scrolled to the last action, then resized");
        }
    }

    /// <summary>The first and the last row of pixels <paramref name="c"/> paints (its ink: every pixel not its background), as it draws itself; (-1, -1) for none.</summary>
    internal static (int Top, int Bottom) InkRows(Control c)
    {
        using var bitmap = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bitmap, new Rectangle(Point.Empty, c.Size));
        int back = bitmap.GetPixel(0, 0).ToArgb(), top = -1, bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).ToArgb() != back)
                {
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
        return (top, bottom);
    }

    /// <summary>
    /// The details' least height in a split (<see cref="DetailsPane.TitleLine"/>) holds their title's first line as the
    /// title draws it: every pixel the title paints for a one-line text lies above it, and it is no more than the title's
    /// whole one-line height. Measured from the drawn title, not from the sum the least is made of.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void TheDetailsLeastHoldsTheTitlesFirstLineAsItIsDrawn(int percent)
    {
        using var d = new Details(percent, "Pool tag Ntfc (ntfs.sys): +1 MB (now 5 MB)", LongGreenLines()[0]);
        d.Panel.Size = new Size(d.P(960), d.P(170)); // a 1024 px window's details
        Assert.Equal(d.Title.HeightAt(short.MaxValue), d.Title.Height); // on one line
        var (top, bottom) = InkRows(d.Title);
        int least = d.Pane.TitleLine, inPane = d.Pane.Content.Top + d.Title.Top + bottom;
        string at = $"at {percent}%: the title {d.Title.Bounds} paints rows {top} to {bottom}, its last in the pane's row {inPane}; the least is {least}";
        Assert.True(top > 0 && bottom >= top, "the title paints nothing " + at);
        Assert.True(inPane < least, "the least cuts the title's line off " + at);
        Assert.True(least <= d.Pane.Padding.Vertical + d.Title.Height, "the least is more than the title's whole one-line height " + at);
    }

    /// <summary>
    /// The green line under the words is docked at the height its text needs at the width the details give it, in the
    /// details' own layout, as the details narrow (a smaller window: it and the title wrap to more lines) and widen again:
    /// it ends at the details' bottom, under the words, and the title stays above them. It set its own height only once
    /// docked at the new width, in the middle of that layout, and kept its top: it ran past the details' bottom when it
    /// grew, and left a gap under itself when it shrank.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    public void TheGreenLineEndsAtTheDetailsBottomAtEveryWidth(int percent)
    {
        using var d = new Details(percent, "File system metadata (Reff, refs.sys): +3.9 GB (now 912 MB)",
            "Not handled yet: emptying the standby cache frees it. You can turn on automatic clearing in Settings.");
        var P = d.P;
        int lines = 0;
        foreach (int width in new[] { P(1036), P(560), P(420), P(1036) })
        {
            d.Panel.Size = new Size(width, P(161)); // one layout of the details, as a resize of the window gives them
            string at = $"details {d.Panel.Size} at {percent}%: title {d.Title.Bounds}, words {d.Words.Bounds}, green line {d.Handled.Bounds}";
            Assert.True(d.Clipped() is [], at + Environment.NewLine + string.Join(Environment.NewLine, d.Clipped()));
            Assert.Equal(d.Title.HeightAt(d.Title.Width), d.Title.Height);
            Assert.Equal(d.Handled.HeightAt(d.Handled.Width), d.Handled.Height);
            Assert.True(d.Title.Top == 0 && d.Title.Bottom <= d.Words.Top && d.Words.Bottom <= d.Handled.Top, at);
            Assert.Equal(d.Panel.ClientSize.Height - d.Panel.Padding.Bottom, d.Handled.Bottom);
            lines = Math.Max(lines, (int)Math.Round((d.Handled.Height - d.Handled.Padding.Vertical - 2) / (double)d.Handled.Font.Height));
        }
        Assert.True(lines >= 2, "the green line never wrapped");
    }
}
