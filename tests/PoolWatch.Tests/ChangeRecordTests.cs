using System.Text.RegularExpressions;
using PoolWatch.Core;
using PoolWatch.Core.Changes;

namespace PoolWatch.Tests;

/// <summary>
/// The records behind Active changes: applied-changes.json round trip, its bounded parse (records are untrusted when
/// read), the per-record rules, and the plain words every row is shown in.
/// </summary>
public class ChangeRecordTests
{
    private static readonly DateTime When = new(2026, 10, 28, 9, 12, 0, DateTimeKind.Utc);
    private static readonly string Hash = new('a', 64);

    internal static ChangeRecord ClearTask(string path = @"\Clear standby", string xml = "<Task/>") => new()
    {
        Id = ChangeIds.RemovedClearTask(path), Kind = RecordKind.RemovedClearTask, ChangedUtc = When, Via = "the setup guide",
        Name = path, TaskXml = xml, TaskLogonType = 5, TaskUserId = "S-1-5-18",
    };

    internal static ChangeRecord Task(string path = @"\Vendor\Updater") => new()
    {
        Id = ChangeIds.ScheduledTask(path), Kind = RecordKind.ScheduledTask, ChangedUtc = When, Via = "the Services tab",
        Name = "Updater", Location = path, EnabledBefore = true,
    };

    internal static ChangeRecord Startup(string location = "HKCU Run", string name = "Helper") => new()
    {
        Id = ChangeIds.StartupEntry(location, name), Kind = RecordKind.StartupEntry, ChangedUtc = When, Via = "the Services tab",
        Name = name, Location = location, EnabledBefore = true,
    };

    internal static ChangeRecord Shortcut(string path = @"C:\Users\someone\Desktop\ChatGPT (no shader cache).lnk") => new()
    {
        Id = ChangeIds.Shortcut(path), Kind = RecordKind.Shortcut, ChangedUtc = When, Via = "a suggestion", Name = "ChatGPT (no shader cache).lnk",
        Path = path, Sha256 = Hash,
    };

    [Fact]
    public void EveryKindRoundTrips()
    {
        var records = new[] { ClearTask(), Task(), Startup(), Shortcut() };
        var back = ChangeRecordFile.Parse(ChangeRecordFile.ToJson(records));
        Assert.Equal(records, back);
        Assert.Contains("\"version\": 1", ChangeRecordFile.ToJson(records));
        Assert.Contains("\"kind\": \"RemovedClearTask\"", ChangeRecordFile.ToJson(records)); // by name, not a number
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\": 2, \"changes\": []}")]
    [InlineData("{\"changes\": []}")]
    [InlineData("{\"version\": 1, \"changes\": {}}")]
    [InlineData("{\"version\": 1, \"changes\": [")]
    [InlineData("")]     // PoolWatch never writes an empty file: a power cut can leave one, and it may have held records
    [InlineData(" \r\n")]
    public void AFileThisBuildCannotReadIsNotRead(string json) => Assert.Null(ChangeRecordFile.Parse(json)); // so it is never written over

    [Fact]
    public void AnEmptyListHoldsNoChanges() => Assert.Empty(ChangeRecordFile.Parse("{\"version\": 1, \"changes\": []}")!);

    /// <summary>
    /// JSON allows a field twice, and the last one counts: the list would read as empty, or a record as another, and the
    /// next save would write that over the file. PoolWatch never writes a field twice, so such a file is not read at all.
    /// </summary>
    [Fact]
    public void AFileWithAFieldWrittenTwiceIsNotRead()
    {
        string good = ChangeRecordFile.ToJson([Task()]);
        Assert.Equal([Task()], ChangeRecordFile.Parse(good));
        Assert.Null(ChangeRecordFile.Parse("""{ "version": 1, "changes": [], "changes": [] }"""));
        Assert.Null(ChangeRecordFile.Parse(good[..good.LastIndexOf('}')] + ", \"changes\": [] }")); // its records, then an empty list
        Assert.Null(ChangeRecordFile.Parse(good.Replace("\"via\": \"the Services tab\"", "\"via\": \"the Services tab\", \"via\": \"the setup guide\""))); // in a record
    }

    /// <summary>
    /// Half of a surrogate pair written as an escape in a field's name: comparing that name with the one looked for
    /// throws, and the file is not read (null), so it is never written over.
    /// </summary>
    [Theory]
    [InlineData("""{ "version": 1, "changes": [], "ve\ud800": 0 }""")]
    [InlineData("""{ "version": 1, "changes": [], "ch\udc00x": 0 }""")]
    public void AFileWithHalfOfASurrogatePairInAFieldNameIsNotRead(string json) => Assert.Null(ChangeRecordFile.Parse(json));

    [Fact]
    public void AnUnknownOrBrokenRecordIsDroppedAlone()
    {
        string good = ChangeRecordFile.ToJson([Task()]);
        string json = good.Replace("\"changes\": [", """
            "changes": [
              { "id": "x", "kind": "SomethingNew", "changedUtc": "2026-10-28T09:12:00Z", "via": "later" },
              { "id": "x", "kind": 3, "changedUtc": "2026-10-28T09:12:00Z", "via": "a number for a kind" },
              { "id": "x", "kind": "Shortcut", "via": "no time" },
              null,
              "text",
            """);
        Assert.Equal([Task()], ChangeRecordFile.Parse(json));
    }

    [Fact]
    public void TheParseIsBounded()
    {
        var many = Enumerable.Range(0, ChangeRecordFile.MaxRecords + 20).Select(i => Startup(name: "Helper" + i)).ToList();
        Assert.Equal(ChangeRecordFile.MaxRecords, ChangeRecordFile.Parse(ChangeRecordFile.ToJson(many))!.Count);

        string huge = ChangeRecordFile.ToJson([Task()]).Replace("\"via\": \"the Services tab\"", $"\"via\": \"the Services tab\", \"pad\": \"{new string('x', ChangeRecordFile.MaxBytes)}\"");
        Assert.Null(ChangeRecordFile.Parse(huge));

        var bigXml = ClearTask(xml: "<Task>" + new string('x', ChangeRecordFile.MaxTaskXmlChars) + "</Task>");
        Assert.Equal([Task()], ChangeRecordFile.Parse(ChangeRecordFile.ToJson([bigXml, Task()])));
    }

    [Fact]
    public void ADuplicateIdKeepsTheFirst()
    {
        var first = Task();
        var second = first with { EnabledBefore = false };
        Assert.Equal([first], ChangeRecordFile.Parse(ChangeRecordFile.ToJson([first, second])));
    }

    /// <summary>
    /// Every entry of the list that is not read is counted and kept, whatever the reason, not only one that cannot be
    /// read as a record at all: a record that fails its checks (here its id does not match its target; a removed task's
    /// definition over 64 KB is another), a second record for a target, and a record past the 500 a file is read for.
    /// A save of the records read alone would lose each of them.
    /// </summary>
    [Theory]
    [InlineData("its id does not match its target")]
    [InlineData("the same record twice")]
    [InlineData("one record past the bound")]
    public void EveryEntryThatIsNotReadIsCountedAndKeptAsItIs(string why)
    {
        List<ChangeRecord> records = why switch
        {
            "its id does not match its target" => [Task(), Task(@"\Vendor\Cleanup") with { Id = @"task:\Other" }],
            "the same record twice" => [Task(), Task()],
            _ => Enumerable.Range(0, ChangeRecordFile.MaxRecords + 1).Select(i => Startup(name: "Helper" + i)).ToList(),
        };
        string json = ChangeRecordFile.ToJson(records);

        var read = ChangeRecordFile.Parse(json, out int unread);

        Assert.Equal(1, unread);
        Assert.Equal(records[..^1], read); // the valid records are listed, the last one is not
        Assert.Equal(records[..^1], ChangeRecordFile.Parse(json, out IReadOnlyList<string> kept));
        string entry = Assert.Single(kept);
        Assert.Equal(records[^1], System.Text.Json.JsonSerializer.Deserialize<ChangeRecord>(entry, ReadAsWritten)); // its text, as in the file
        Assert.Contains(entry, json);
    }

    /// <summary>The entries a file could not be read for are written back after its records exactly as they were, so a save loses none of them.</summary>
    [Fact]
    public void TheEntriesNotReadAreWrittenBackAsTheyWere()
    {
        string good = ChangeRecordFile.ToJson([Task()]);
        const string newer = """{ "id": "later:x", "kind": "SomethingNew", "changedUtc": "2026-10-28T09:12:00Z", "via": "a newer PoolWatch", "more": [1, "é"] }""";
        string json = good.Replace("\"changes\": [", "\"changes\": [\n" + newer + ", null, 7,");
        Assert.Equal([Task()], ChangeRecordFile.Parse(json, out IReadOnlyList<string> kept));
        Assert.Equal([newer, "null", "7"], kept);

        string saved = ChangeRecordFile.ToJson([Startup()], kept);

        Assert.Contains(newer, saved);
        Assert.Equal([Startup()], ChangeRecordFile.Parse(saved, out IReadOnlyList<string> again));
        Assert.Equal(kept, again);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ReadAsWritten = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static TheoryData<string, ChangeRecord> InvalidRecords() => new()
    {
        { "id does not match", Task() with { Id = "task:\\Other" } },
        { "no via", Task() with { Via = "" } },
        { "control character", Startup() with { Name = "Helper\n" } },
        { "Microsoft task", Task(@"\Microsoft\Windows\Defrag\ScheduledDefrag") },
        { "Microsoft clear task", ClearTask(@"\MicrosoftClear") },
        { "relative task path", Task(@"Vendor\Updater") },
        { "dot segment", Task(@"\Vendor\..\Updater") },
        { "task path with a forward slash", Task(@"\Vendor/Updater") },
        { "no state before", Task() with { EnabledBefore = null } },
        { "clear task in a folder", ClearTask(@"\Tools\Clear standby") },
        { "clear task in a folder by a forward slash", ClearTask(@"\Tools/Clear standby") },
        { "clear task without definition", ClearTask() with { TaskXml = null } },
        { "clear task definition too long", ClearTask(xml: new string('x', ChangeRecordFile.MaxTaskXmlChars + 1)) },
        { "clear task without logon type", ClearTask() with { TaskLogonType = null } },
        { "clear task with an unknown logon type", ClearTask() with { TaskLogonType = 9 } },
        { "unknown startup location", Startup("HKCU RunOnce") },
        { "startup entry without a name", Startup() with { Name = null } },
        { "shortcut that is not a .lnk", Shortcut(@"C:\Users\someone\Desktop\run.exe") },
        { "shortcut on a share", Shortcut(@"\\server\share\Desktop\x.lnk") },
        { "shortcut with a stream", Shortcut(@"C:\Users\someone\Desktop\x.lnk:evil.lnk") },
        { "shortcut without a hash", Shortcut() with { Sha256 = null } },
        { "shortcut with a short hash", Shortcut() with { Sha256 = "abc" } },
        { "shortcut with a hash that is not hex", Shortcut() with { Sha256 = new string('z', 64) } },
    };

    [Theory]
    [MemberData(nameof(InvalidRecords))]
    public void AnInvalidRecordIsNotActedOn(string why, ChangeRecord record)
    {
        Assert.True(ChangeRecordFile.Invalid(record) is not null, why);
        Assert.Empty(ChangeRecordFile.Parse(ChangeRecordFile.ToJson([record]))!); // dropped on read too
    }

    [Fact]
    public void AKindThisBuildDoesNotKnowIsNotActedOn() => Assert.NotNull(ChangeRecordFile.Invalid(Task() with { Kind = (RecordKind)42 }));

    [Fact]
    public void ValidRecordsPass()
    {
        foreach (var r in new[] { ClearTask(), Task(), Startup(), Shortcut(), Startup("Startup folder (you)", "Tool.lnk"), ClearTask() with { TaskUserId = null } })
            Assert.Null(ChangeRecordFile.Invalid(r));
    }

    [Fact]
    public void IdsAreDerivedFromTheTarget()
    {
        Assert.Equal("service:mapsbroker", ChangeIds.Service("MapsBroker"));
        Assert.Equal(@"clear-task:\Clear standby", ChangeIds.RemovedClearTask(@"\Clear standby"));
        Assert.Equal(@"task:\Vendor\Updater", ChangeIds.ScheduledTask(@"\Vendor\Updater"));
        Assert.Equal(@"startup:HKCU Run\Helper", ChangeIds.StartupEntry("HKCU Run", "Helper"));
        Assert.Equal(@"shortcut:C:\x.lnk", ChangeIds.Shortcut(@"C:\x.lnk"));
    }

    [Theory]
    [InlineData(1, 0, "The cache is cleared every hour")]
    [InlineData(2.5, 0, "The cache is cleared every 2.5 hours")]
    [InlineData(2, 0, "The cache is cleared every 2 hours")]
    [InlineData(0.5, 0, "The cache is cleared every 30 minutes")]
    [InlineData(0, 8, "The cache is cleared when the kernel pool reaches 8 GB")]
    [InlineData(1, 8, "The cache is cleared every hour, or sooner when the kernel pool reaches 8 GB")]
    [InlineData(1, 7.5, "The cache is cleared every hour, or sooner when the kernel pool reaches 7.5 GB")]
    [InlineData(0, 0, "Automatic cache clearing is on, with no schedule and no size rule")]
    public void TheAutomaticClearInPlainWords(double hours, double gb, string expected) => Assert.Equal(expected, ChangeWords.AutoClear(hours, gb));

    [Fact]
    public void EachRecordedKindInPlainWords()
    {
        Assert.Equal("Replaced your own clear task \"Clear standby\" (it ran RAMMap)", ChangeWords.Of(ClearTask()));
        Assert.Equal(@"Turned off the scheduled task \Vendor\Updater", ChangeWords.Of(Task()));
        Assert.Equal(@"Turned on the scheduled task \Vendor\Updater", ChangeWords.Of(Task() with { EnabledBefore = false }));
        Assert.Equal("Turned off the startup app Helper", ChangeWords.Of(Startup()));
        Assert.Equal("Created the desktop shortcut \"ChatGPT (no shader cache)\"", ChangeWords.Of(Shortcut()));
        Assert.Equal("Turned off Downloaded Maps Manager (MapsBroker)", ChangeWords.Service("Downloaded Maps Manager", "MapsBroker", turnedOff: true));
        Assert.Equal("Set Fax back to its Windows default", ChangeWords.Service("Fax", "Fax", turnedOff: false));
        Assert.Equal("Turned off MapsBroker", ChangeWords.Service("", "MapsBroker", turnedOff: true));
        // A row a profile made says which (an Apply by hand, or a built-in, says nothing more).
        Assert.Equal("Turned off MapsBroker (profile Build box, at start)", ChangeWords.Service("", "MapsBroker", turnedOff: true, "profile Build box, at start"));
        Assert.Equal("Turned off MapsBroker (profile Build box, edited)", ChangeWords.Service("", "MapsBroker", turnedOff: true, "profile Build box, edited"));
        Assert.Equal("Turned off MapsBroker", ChangeWords.Service("", "MapsBroker", turnedOff: true, "manual"));
        Assert.Equal("Turned off MapsBroker", ChangeWords.Service("", "MapsBroker", turnedOff: true, "Telemetry only"));
        Assert.Equal("Build box, the profile PoolWatch applies when it starts, turns Maps (MapsBroker) off again then. Putting it back also takes it out of Build box, so it stays on.",
            ChangeWords.KeptOffAtStart("Build box", "Maps (MapsBroker)"));
    }

    [Fact]
    public void TheGuardsNoteSaysItIsTurnedOnAgainEachTimePoolWatchStarts()
    {
        // The guard's check runs each time the tray app starts (from the sign-in task or by hand), not at a sign-in as such.
        Assert.Contains("PoolWatch turns it on again each time it starts. Turn off stops that", ChangeWords.TokenGuardNote);
        Assert.DoesNotContain("sign-in", ChangeWords.TokenGuardNote);
    }

    [Fact]
    public void TheWordsHoldNoSettingNamesOrCodes()
    {
        var texts = new List<string>
        {
            ChangeWords.LogonTask, ChangeWords.TokenGuard, ChangeWords.TokenGuardNote, ChangeWords.TokenGuardOff, ChangeWords.AutoClearKeepsTaskNote, ChangeWords.NotListed,
            ChangeWords.AutoClear(1, 8), ChangeWords.AutoClear(0, 0), ChangeWords.Service("Downloaded Maps Manager", "maps", true, "profile Build box, at start"),
            ChangeWords.KeptOffAtStart("Build box", "maps"),
            ChangeWords.Of(ClearTask()), ChangeWords.Of(Task()), ChangeWords.Of(Startup()), ChangeWords.Of(Shortcut()),
            ChangeWords.Unread("the services it changed"), ChangeWords.UnreadNote(""),
        };
        var settingNames = typeof(Settings).GetProperties().Select(p => p.Name).ToList();
        // camelCase or a run-together PascalCase word ("AutoClearEnabled"); PoolWatch is the app's name.
        var code = new Regex(@"\b([a-z]+[A-Z]\w*|[A-Z][a-z]+[A-Z][a-z]+\w*)\b");
        foreach (var text in texts)
        {
            Assert.DoesNotContain(settingNames, n => text.Contains(n, StringComparison.Ordinal));
            Assert.DoesNotMatch(code, text.Replace("PoolWatch", ""));
            Assert.DoesNotContain("!", text);
        }
    }
}
