using System.IO.Compression;
using PoolWatch.Core;
using PoolWatch.Core.Boot;
using PoolWatch.Core.Experiments;
using PoolWatch.Core.Filters;
using PoolWatch.Core.Findings;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Tags;
using PoolWatch.Core.Versions;

namespace PoolWatch.Tests;

/// <summary>C6 (review Part 2): the smaller bugs, each fixed in Core and tested here.</summary>
public class SmallFixesTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pw-c6-" + Guid.NewGuid().ToString("N"));

    public SmallFixesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---- 1. the boot summary counts every incident since boot, not only the last 6 hours'

    [Fact]
    public void CreatedCountKeepsEveryIncidentAfterTheSixHourPrune()
    {
        var w = new World();
        var settings = new Settings();
        var mgr = new IncidentManager(settings, new TagKnowledge(null, null), new FakeHost(_dir), w.Registry);
        var det = new PoolWatch.Core.Detection.Detector(settings);
        for (int i = 0; i < 3; i++) w.Sample(("File", 10 * Settings.MB));
        mgr.CreateCapture(w.History, [PoolTag.Parse("File")], w.Now);
        mgr.CreateCapture(w.History, [PoolTag.Parse("File")], w.Now.AddSeconds(1));
        var lost = mgr.CreateCapture(w.History, [PoolTag.Parse("File")], w.Now.AddSeconds(2));
        mgr.Discard(lost); // a capture whose trace never started is not an incident
        Assert.Equal(2, mgr.CreatedCount);
        mgr.Process(w.History, det, [], w.Now.AddHours(7));
        Assert.Empty(mgr.Closed);              // dropped from memory after 6 h (the folders stay)
        Assert.Equal(2, mgr.CreatedCount);     // still counted for the boot summary
    }

    // ---- 2. TEMP is redacted as <TEMP>, never as <HOME>

    [Fact]
    public void TempIsRedactedAsTempNotHome()
    {
        var r = new RedactionInfo("alice", "BIGBOX", [@"C:\Users\alice", @"C:\Users\ALICE~1"], "BIGBOX", null,
            [@"C:\Users\alice\AppData\Local\Temp", @"C:\Users\ALICE~1\AppData\Local\Temp"]);
        Assert.Equal(@"<TEMP>\x.etl, <HOME>\y, <TEMP>\z and <HOME>\Documents",
            Redactor.Redact(@"C:\Users\alice\AppData\Local\Temp\x.etl, C:\Users\alice\y, C:\Users\ALICE~1\AppData\Local\Temp\z and C:\Users\ALICE~1\Documents", r));
        Assert.Equal(@"""<TEMP>\\x""", Redactor.Redact(@"""C:\\Users\\alice\\AppData\\Local\\Temp\\x""", r)); // JSON-escaped form
        Assert.Equal("<TEMP>/a", Redactor.Redact("C:/Users/alice/AppData/Local/Temp/a", r));
        // TEMP equal to a home path (odd, but possible): TEMP still wins the tie.
        Assert.Equal(@"<TEMP>\q", Redactor.Redact(@"D:\scratch\q", new RedactionInfo("alice", "PC", [@"D:\scratch"], TempPaths: [@"D:\scratch"])));
    }

    // ---- 3. AppModel / filter events have their own buffer and never evict other findings

    [Fact]
    public void EventFindingsCannotEvictOtherFindings()
    {
        var log = new FindingLog();
        log.Add(new Finding { TimeUtc = T0, Kind = FindingKind.Crash, Severity = FindingSeverity.Alert, Title = "keep me" });
        for (int i = 0; i < 5000; i++)
            log.Add(new Finding { TimeUtc = T0.AddSeconds(i), Kind = i % 2 == 0 ? FindingKind.PackageActivation : FindingKind.FilterEvent, Title = $"event {i}" });
        var snap = log.Snapshot();
        Assert.Contains(snap, f => f.Title == "keep me");
        Assert.Equal(1 + FindingLog.MaxKeptEvents, log.Count);
        Assert.Equal("keep me", snap[0].Title);                  // merged in the order added
        Assert.Equal("event 4999", snap[^1].Title);
        // A filter warning (a load paired with a package activation) is a real finding: the main buffer.
        Assert.False(FindingLog.IsHighVolume(new Finding { TimeUtc = T0, Kind = FindingKind.FilterEvent, Severity = FindingSeverity.Warning, Title = "pair" }));
        log.Add(new Finding { TimeUtc = T0, Kind = FindingKind.Mark, Title = "last" });
        Assert.Equal("last", log.Snapshot()[^1].Title);
        Assert.Single(log.Between(T0, T0), f => f.Kind == FindingKind.Mark);
    }

    // ---- 4. crash records are paged through, never skipped

    [Fact]
    public void CrashCursorPagesThroughABacklogWithoutSkipping()
    {
        // A log of 2,500 matching records (the old reader kept the newest 200 and skipped the rest for good).
        var ids = Enumerable.Range(1, 2500).Select(i => (long)i).ToList();
        var cursor = CrashCursor.Parse(null, T0);
        var read = new List<long>();
        for (int check = 0; check < 5; check++)
        {
            var (since, after) = cursor.Next("Application", T0, 7, newestInLog: 2500);
            Assert.Equal(T0.AddDays(check == 0 ? -7 : 0), since);
            var page = ids.Where(id => after is null || id > after).Take(CrashCursor.PageSize).ToList();
            read.AddRange(page);
            cursor.Advance("Application", T0, page.Count > 0 ? page[^1] : null, 2500, page.Count >= CrashCursor.PageSize);
        }
        Assert.Equal(ids, read); // every record exactly once, oldest first
        Assert.Equal(2500, cursor.AfterRecord("Application"));
    }

    [Fact]
    public void CrashCursorStateRoundTripsAndToleratesOldAndBadFiles()
    {
        // A file written by the previous version (one line of ticks): read from that time, then by record ID.
        var legacy = CrashCursor.Parse(T0.AddHours(-3).Ticks.ToString(), T0);
        Assert.Equal((T0.AddHours(-3), (long?)null), legacy.Next("System", T0, 7, newestInLog: 90));
        // A quiet log (no matching record) still gets a cursor: the newest record seen before the read.
        legacy.Advance("System", T0, lastRead: null, newestBefore: 90, pageFull: false);
        Assert.Equal(90, legacy.AfterRecord("System"));
        legacy.Advance("Application", T0, lastRead: 1234, newestBefore: 1300, pageFull: true);

        var again = CrashCursor.Parse(legacy.Format(), T0.AddMinutes(1));
        Assert.Equal(90, again.AfterRecord("System"));
        Assert.Equal(1234, again.AfterRecord("Application")); // a full page: continue right after the last record read
        Assert.Equal((T0, (long?)1234), again.Next("Application", T0.AddMinutes(1), 7, newestInLog: 1400));
        // The log was cleared (its record IDs start again): back to time, from the last query.
        Assert.Equal((T0, (long?)null), again.Next("Application", T0.AddMinutes(1), 7, newestInLog: 5));
        // Garbage, or a time in the future: the lookback.
        Assert.Equal((T0.AddDays(-7), (long?)null), CrashCursor.Parse("not a number\nSystem x y\n", T0).Next("System", T0, 7, null));
        Assert.Equal((T0.AddDays(-7), (long?)null), CrashCursor.Parse(T0.AddDays(1).Ticks.ToString(), T0).Next("System", T0, 7, null));
        // The queries: by record ID when the cursor has one, else by time (the old form, unchanged).
        Assert.Contains("EventRecordID > 1234", CrashMarkers.XPath(CrashMarkers.Logs[1].filter, T0, T0, 1234));
        Assert.Contains("timediff(@SystemTime) <= 3600000", CrashMarkers.XPath(CrashMarkers.Logs[1].filter, T0.AddHours(-1), T0, null));
        Assert.Equal(["System", "Application"], CrashMarkers.Logs.Select(l => l.channel));
    }

    [Fact]
    public void OlderAppCrashesAreSummarisedNotDropped()
    {
        var apps = Enumerable.Range(0, 60).Select(i => new CrashRecord(T0.AddMinutes(i), CrashKind.AppCrash, i % 3 == 0 ? "node.exe" : "ChatGPT.exe", null, null, $"crash {i}")).Reverse().ToList();
        var f = CrashMarkers.AppFindings(apps);
        Assert.Equal(51, f.Count);
        Assert.StartsWith("App crashes: 10 older ones", f[0].Title);
        Assert.Equal(10, f[0].Details.Count);
        Assert.Contains("crash 0", f[0].Details[0]);
        Assert.Equal("App crash: crash 10", f[1].Title);
        Assert.Equal("App crash: crash 59", f[^1].Title);
        Assert.Equal(5, CrashMarkers.AppFindings(apps.Take(5).ToList()).Count); // under the limit: one each, no summary
    }

    // ---- 6. a Microsoft binary signed through a catalog is Microsoft

    [Fact]
    public void MicrosoftSignerCoversCatalogAndOrganization()
    {
        Assert.True(ServiceCatalogue.MicrosoftSigner("Microsoft Windows", underWindowsDir: false));            // catalog-signed, under Program Files
        Assert.True(ServiceCatalogue.MicrosoftSigner(".NET", false, "Microsoft Corporation"));                 // CN=.NET, O=Microsoft Corporation
        Assert.False(ServiceCatalogue.MicrosoftSigner("Contoso Ltd", false, "Contoso Ltd"));
        Assert.False(ServiceCatalogue.MicrosoftSigner("Contoso Ltd", underWindowsDir: true));                 // a third party under %WINDIR%
        Assert.True(ServiceCatalogue.MicrosoftSigner(null, underWindowsDir: true));                           // unsigned under %WINDIR%
        Assert.False(ServiceCatalogue.MicrosoftSigner(null, underWindowsDir: false));
        // And the grouping that depends on it: Microsoft and not catalogued is protected; a catalogued one stays optional.
        var cat = ServiceCatalogue.Load("""[{ "services": ["DiagTrack"], "category": "Telemetry", "lose": "nothing" }]""");
        Assert.Equal(ServiceGroup.Protected, cat.GroupOf("SomeMsService", ServiceCatalogue.MicrosoftSigner("Microsoft Windows", false), false));
        Assert.Equal(ServiceGroup.Optional, cat.GroupOf("DiagTrack", true, false));
        Assert.Equal(ServiceGroup.Unknown, cat.GroupOf("ContosoUpdater", ServiceCatalogue.MicrosoftSigner("Contoso Ltd", false), false));
    }

    // ---- 8. a failed baseline write backs off; Observe never writes

    private static Sample S(DateTime t, long npMb) =>
        new(t, new PoolTotals(500UL << 20, (ulong)npMb << 20, 0, 0),
            [new PoolTagEntry(PoolTag.Parse("File"), 0, 0, 0, 10, 1, 80UL << 20), new PoolTagEntry(PoolTag.Parse("MmSt"), 1, 0, 300UL << 20, 0, 0, 0)], [])
        { System = new SystemCounters(8UL << 30, 32UL << 30, 0, 0, 90_000, 250) };

    [Fact]
    public void FailedBaselineWriteBacksOffAndObserveNeverWrites()
    {
        int checks = 0, baselineWrites = 0;
        bool fail = true;
        string Check(string p)
        {
            checks++;
            if (p.EndsWith("baseline.json.tmp", StringComparison.Ordinal)) { baselineWrites++; if (fail) throw new IOException("disk full"); }
            return p;
        }
        var tr = new BootTracker(_dir, Check);
        tr.Observe(S(T0, 400), TimeSpan.FromMinutes(3), 0);
        Assert.Equal(0, checks);                 // in memory only: Observe runs under the history lock
        var baseline = tr.Baseline;
        Assert.NotNull(baseline);
        Assert.Contains("retry in 5 min", tr.Flush(T0));
        Assert.Equal(1, baselineWrites);
        for (int i = 1; i <= 60; i++)            // a minute of samples
        {
            tr.Observe(S(T0.AddSeconds(i), 400 + i), TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(i), 0);
            tr.Flush(T0.AddSeconds(i));
        }
        Assert.Same(baseline, tr.Baseline);      // not rebuilt every sample
        Assert.Equal(1, baselineWrites);         // and not rewritten: backed off
        Assert.True(tr.BaselinePending);
        fail = false;
        Assert.Null(tr.Flush(T0 + BootTracker.RetryAfterFailure));
        Assert.Equal(2, baselineWrites);
        Assert.False(tr.BaselinePending);
        Assert.True(File.Exists(Path.Combine(_dir, baseline!.BootId, "baseline.json")));
        Assert.True(File.Exists(Path.Combine(_dir, baseline.BootId, "summary.json")));
    }

    // ---- 9. zip entry names: Reff / RefF and forbidden characters

    [Fact]
    public void BundleEntryNamesAreSafeAndUnique()
    {
        string a = VendorBundle.DuringGrowthEntry(PoolTag.Parse("Reff"), T0), b = VendorBundle.DuringGrowthEntry(PoolTag.Parse("RefF"), T0);
        Assert.False(a.Equals(b, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Reff-66666552", a);     // readable form + hex of the raw bytes
        string odd = VendorBundle.DuringGrowthEntry(PoolTag.FromBytes("<:*?"u8), T0);
        Assert.DoesNotContain(odd["tags/".Length..], c => "<>:\"|?*\\/".Contains(c));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("tags/a.csv", VendorBundle.EntryName("tags/a.csv", used));
        Assert.Equal("tags/A-2.csv", VendorBundle.EntryName("tags/A.csv", used));
        Assert.Equal("tags/x_y_.csv", VendorBundle.EntryName(@"tags\x<y>.csv", used));
        Assert.Equal("_CON.csv", VendorBundle.EntryName("CON.csv", used));
        Assert.Equal("dots", VendorBundle.EntryName("dots. ", used));

        var zip = VendorBundle.Write(_dir, T0, new Dictionary<string, string> { ["tags/Reff.csv"] = "1", ["tags/RefF.csv"] = "2", ["a|b.md"] = "3" }, null, p => p);
        using var z = ZipFile.OpenRead(zip);
        var names = z.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("a_b.md", names);
    }

    // ---- 10. marks: no listing every tick; undeletable files never block new marks

    [Fact]
    public void UndeletableMarkFilesDoNotBlockNewOnesAndTheFolderIsNotListedEveryTick()
    {
        for (int n = 0; n < Marks.MaxPerTick; n++) File.WriteAllText(Path.Combine(_dir, Marks.RequestName(T0.AddSeconds(n), 1000 + n)), $"stuck {n}");
        File.WriteAllText(Path.Combine(_dir, Marks.RequestName(T0.AddMinutes(5), 1)), "new mark");
        // Files 1000+ cannot be deleted (the write policy refuses them, as a planted or locked file would fail).
        string Check(string p) => Path.GetFileNameWithoutExtension(p).Split('-')[^1].Length == 4 ? throw new UnauthorizedAccessException(p) : p;
        DateTime? stamp = T0;
        var inbox = new MarkInbox(_dir, Check, _ => stamp);

        var first = inbox.Take(T0);
        Assert.Equal(Marks.MaxPerTick, first.Count);                      // the stuck ones are recorded once...
        var second = inbox.Take(T0.AddSeconds(1));
        Assert.Equal("new mark", Assert.Single(second).text);            // ...and do not hold the new one back
        Assert.False(File.Exists(Path.Combine(_dir, Marks.RequestName(T0.AddMinutes(5), 1))));
        Assert.Empty(inbox.Take(T0.AddSeconds(2)));                       // not reported again
        int listings = inbox.Listings;
        for (int i = 3; i < 30; i++) Assert.Empty(inbox.Take(T0.AddSeconds(i)));
        Assert.Equal(listings, inbox.Listings);                           // folder unchanged: not listed every tick

        File.WriteAllText(Path.Combine(_dir, Marks.RequestName(T0.AddMinutes(6), 2)), "later");
        stamp = T0.AddSeconds(30);                                        // the folder changed
        Assert.Equal("later", Assert.Single(inbox.Take(T0.AddSeconds(30))).text);
        Assert.Empty(inbox.Take(T0.AddSeconds(31) + MarkInbox.RelistEvery)); // the safety-net relisting finds nothing new
        Assert.Equal(listings + 2, inbox.Listings);
    }

    [Fact]
    public void AMarkStillBeingWrittenIsRetriedThenGivenUp()
    {
        string path = Path.Combine(_dir, Marks.RequestName(T0, 7));
        DateTime? stamp = T0;
        var inbox = new MarkInbox(_dir, p => p, _ => stamp);
        using (var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            held.Write("busy"u8);
            Assert.Empty(inbox.Take(T0));
            Assert.Empty(inbox.Take(T0.AddSeconds(1))); // retried (stamp unchanged, but a retry is pending)
        }
        Assert.Equal("busy", Assert.Single(inbox.Take(T0.AddSeconds(2))).text);

        // One that never becomes readable is given up after MaxAttempts and no longer forces a listing every tick.
        string stuck = Path.Combine(_dir, Marks.RequestName(T0, 8));
        using var forever = new FileStream(stuck, FileMode.Create, FileAccess.Write, FileShare.None);
        stamp = T0.AddSeconds(3);
        for (int i = 0; i < MarkInbox.MaxAttempts; i++) Assert.Empty(inbox.Take(T0.AddSeconds(3 + i)));
        int listings = inbox.Listings;
        Assert.Empty(inbox.Take(T0.AddSeconds(20)));
        Assert.Equal(listings, inbox.Listings);
    }

    // ---- 11. FilterManager unload / load by event ID, not by English message text

    [Fact]
    public void FilterEventsAreClassifiedByEventId()
    {
        var tl = new FilterTimeline();
        // A German system: no "unload" in the text; the filter name is insertion string %5.
        var unload = Assert.Single(tl.OnLogEvent(new LogEvent(T0, "System", FilterTimeline.FilterManagerProvider, 1,
            "Das Dateisystemfilter \"wcifs\" wurde erfolgreich entladen.", ["0", "10", "0", "", "wcifs", "2026-01-01"])));
        Assert.Equal("unload", unload.Data["action"]);
        Assert.Equal("wcifs", unload.Image);
        // Event 14 says "loaded", but it is an attach policy, not a filter load.
        var policy = Assert.Single(tl.OnLogEvent(new LogEvent(T0, "System", FilterTimeline.FilterManagerProvider, 14,
            "Filter Manager successfully loaded filter attach policy for this volume.", [])));
        Assert.Equal("other", policy.Data["action"]);
        // A load with no message at all (the description could not be formatted): ID 6 and %5.
        var load = Assert.Single(tl.OnLogEvent(new LogEvent(T0, "System", FilterTimeline.FilterManagerProvider, 6, null, ["0", "10", "0", "", "bindflt", "x"])));
        Assert.Equal("load", load.Data["action"]);
        Assert.Equal("bindflt", load.Image);
    }
}
