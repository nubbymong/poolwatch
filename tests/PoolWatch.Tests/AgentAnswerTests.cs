using System.Text;
using PoolWatch.Core.Services;
using PoolWatch.Core.Services.Agent;

namespace PoolWatch.Tests;

/// <summary>
/// Reading the AI agent's answer: strict about its form, bounded, a name only ever picks an item on this PC now, and every
/// service goes through Apply's gate, so protected and Insider services, drivers and anything not on the PC are refused
/// with why. The result is a plan only: nothing in the answer is run or opened, and nothing is changed.
/// </summary>
public class AgentAnswerTests
{
    private static AgentPlan Plan(string json, AgentItemList? list = null)
    {
        var r = AgentAnswer.Read(json, list ?? AgentPc.Small());
        Assert.True(r.Error is null, r.Error);
        return r.Plan!;
    }

    private static string Refused(string json, AgentItemList? list = null)
    {
        var r = AgentAnswer.Read(json, list ?? AgentPc.Small());
        Assert.Null(r.Plan);
        return Assert.IsType<string>(r.Error);
    }

    /// <summary>The prompt's own example, read against a PC that has its made-up items, plans exactly what it says.</summary>
    [Fact]
    public void TheDocumentedExampleIsRead()
    {
        var list = AgentPc.List(
            [AgentPc.Svc("ExampleService", "Example Service", ms: false), AgentPc.Svc("OtherService", "Other Service", ms: false)],
            [new AgentStartupEntry("Example App", "HKCU Run", "Example Ltd", true, false)],
            [new AgentScheduledTask(@"\Example\Example Task", "Example Task", "Example Ltd", true)]);
        var plan = Plan(AgentPrompt.ExampleAnswer, list);
        var s = Assert.Single(plan.Services);
        Assert.Equal(("ExampleService", "You said you never use it.", ServiceStartType.Disabled), (s.Option.Service.Name, s.Item.Reason, s.NewStart));
        Assert.Equal(("Example App", "HKCU Run"), (Assert.Single(plan.Startup).Entry.Name, plan.Startup[0].Entry.Location));
        Assert.Equal(@"\Example\Example Task", Assert.Single(plan.Tasks).Task.Path);
        Assert.Equal("Other Service (OtherService)", Assert.Single(plan.Kept).Name);
        Assert.Empty(plan.Refused);
        Assert.Empty(plan.AlreadyOff);
        Assert.True(plan.HasChanges);
        Assert.Equal(["ExampleService"], plan.ServiceNames);
    }

    [Fact]
    public void AnAnswerOnlyTicksWhatIsOnThePcAndSaysWhatItLeftOut()
    {
        var plan = Plan("""
            { "version": 1,
              "turnOff": { "services": ["MapsBroker", "lfsvc", "Fax", "NoSuchService"], "startup": ["Example Deck", "Old Launcher", "OneDrive"],
                           "tasks": ["Example Power Notice", "Maintenance"] },
              "keep": { "services": ["ExampleVendorSvc", "NotHere"] },
              "why": { "MapsBroker": "No maps.", "Unrelated": "ignored" } }
            """);
        Assert.Equal(["MapsBroker", "lfsvc", "Fax"], plan.ServiceNames); // Fax is not running, but it is on the PC and may be turned off
        Assert.Equal("No maps.", plan.Services[0].Item.Reason);
        Assert.Equal("Downloaded Maps Manager (MapsBroker)", plan.Services[0].Item.Name);
        Assert.Equal(["Example Deck"], plan.Startup.Select(x => x.Entry.Name));
        Assert.Equal(["Example Power Notice"], plan.Tasks.Select(x => x.Task.Name));
        Assert.Equal(["Old Launcher"], plan.AlreadyOff.Select(x => x.Name));
        Assert.Equal(["NoSuchService", "OneDrive", "Maintenance"], plan.Refused.Select(x => x.Item.Id));
        Assert.Contains("not a service on this PC now", plan.Refused[0].Why);
        Assert.Contains("Microsoft's are left alone", plan.Refused[1].Why);
        Assert.Equal(["Example Device Service (ExampleVendorSvc)"], plan.Kept.Select(k => k.Name));
        Assert.Equal(["NotHere"], plan.KeepNotFound);
    }

    /// <summary>Protected services, an Insider service while the PC is enrolled, and a driver are refused with why, never planned.</summary>
    [Fact]
    public void ProtectedInsiderAndDriverServicesAreRefusedWithWhy()
    {
        var list = AgentPc.Small(insider: true, more: [AgentPc.Svc("wisvc", "Windows Insider Service", ServiceStartType.Manual),
            AgentPc.Svc("exampledrv", "Example Driver", ms: false, win32: false), AgentPc.Svc("SomeMicrosoftSvc", "Some Microsoft Service")]);
        var plan = Plan("""{ "version": 1, "turnOff": { "services": ["WinDefend", "wisvc", "exampledrv", "SomeMicrosoftSvc", "wuauserv"] } }""", list);
        Assert.Empty(plan.Services);
        Assert.False(plan.HasChanges);
        var why = plan.Refused.ToDictionary(r => r.Item.Id, r => r.Why);
        Assert.Equal("PoolWatch never changes it. It is a protected service: Windows security.", why["WinDefend"]);
        Assert.Equal("PoolWatch never changes it. It is a protected service: Windows Insider service (the PC is enrolled).", why["wisvc"]);
        Assert.Contains("driver or kernel service", why["exampledrv"]);
        Assert.Equal("It is a Windows service PoolWatch does not list, which it leaves alone.", why["SomeMicrosoftSvc"]);
        Assert.Equal("PoolWatch never changes it. It is a protected service: Windows Update.", why["wuauserv"]); // not installed here
    }

    [Fact]
    public void AServiceOffAlreadyIsSaidAndNotPlanned()
    {
        var list = AgentPc.Small(more: [AgentPc.Svc("SysMain", "SysMain", ServiceStartType.Disabled, running: false)]);
        var plan = Plan("""{ "version": 1, "turnOff": { "services": ["SysMain"] } }""", list);
        Assert.Empty(plan.Services);
        Assert.Equal(["SysMain"], plan.AlreadyOff.Select(a => a.Id));
    }

    [Fact]
    public void ANameBothTurnedOffAndKeptIsLeftAsItIs()
    {
        var plan = Plan("""{ "version": 1, "turnOff": { "services": ["MapsBroker"] }, "keep": { "services": ["mapsbroker"] } }""");
        Assert.Empty(plan.Services);
        Assert.Contains("both turns it off and keeps it", Assert.Single(plan.Refused).Why);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void NamesMatchInAnyCaseAndTwiceIsOnce()
    {
        var plan = Plan("""{ "version": 1, "turnOff": { "services": ["mapsbroker", "MAPSBROKER"], "startup": ["example deck"] } }""");
        Assert.Equal(["MapsBroker"], plan.ServiceNames);
        Assert.Equal("Example Deck", Assert.Single(plan.Startup).Entry.Name);
    }

    /// <summary>A name two items share is refused, never guessed: which one is meant cannot be told.</summary>
    [Fact]
    public void ANameThatMatchesTwoItemsIsRefused()
    {
        // Two tasks in one folder whose names differ only by an e-mail address: redacted, even their paths read the same.
        var list = AgentPc.List([], [], [new AgentScheduledTask(@"\Vendor\a@example.com sync", "a@example.com sync", null, true),
            new AgentScheduledTask(@"\Vendor\b@example.com sync", "b@example.com sync", null, true)]);
        Assert.Equal(2, list.Find(AgentItemKind.Task, @"\Vendor\<EMAIL> sync").Count);
        var plan = Plan("""{ "version": 1, "turnOff": { "tasks": ["\\Vendor\\<EMAIL> sync"] } }""", list);
        Assert.Empty(plan.Tasks);
        Assert.Contains("more than one item", Assert.Single(plan.Refused).Why);
    }

    /// <summary>A task that is disabled already is said, not planned: disabling it again would record a change PoolWatch never made.</summary>
    [Fact]
    public void ATaskOffAlreadyIsSaidAndNotPlanned()
    {
        var list = AgentPc.List([], [], [new AgentScheduledTask(@"\Example\Example Report", "Example Report", null, Enabled: false),
            new AgentScheduledTask(@"\Example\Example Sync", "Example Sync", null, Enabled: true)]);
        var plan = Plan("""{ "version": 1, "turnOff": { "tasks": ["Example Report", "Example Sync"] } }""", list);
        Assert.Equal(["Example Sync"], plan.Tasks.Select(t => t.Task.Name));
        Assert.Equal(["Example Report"], plan.AlreadyOff.Select(a => a.Id));
    }

    /// <summary>The answer names an item as the prompt showed it, redacted; the plan acts on the real entry behind it.</summary>
    [Fact]
    public void ARedactedNameStillPicksItsItem()
    {
        var list = AgentPc.List([], [new AgentStartupEntry(AgentPc.User + " backup", "HKCU Run", null, true, false)],
            [new AgentScheduledTask(@"\ExampleUpdaterTaskUser" + AgentPc.Sid, "ExampleUpdaterTaskUser" + AgentPc.Sid, null, true)]);
        var plan = Plan("""{ "version": 1, "turnOff": { "startup": ["<USER> backup"], "tasks": ["ExampleUpdaterTaskUser<SID>"] } }""", list);
        Assert.Equal(AgentPc.User + " backup", Assert.Single(plan.Startup).Entry.Name);
        Assert.Equal(@"\ExampleUpdaterTaskUser" + AgentPc.Sid, Assert.Single(plan.Tasks).Task.Path);
        // The real name is not in what the agent sees, so naming it does not find it; the review says the list named it
        // differently, not that it is missing (an agent on this PC can read the real names).
        Assert.Equal(AgentAnswer.NamedDifferently, Assert.Single(Plan($$"""{ "version": 1, "turnOff": { "startup": ["{{AgentPc.User}} backup"] } }""", list).Refused).Why);
    }

    /// <summary>Two startup entries of one name, in two places, are told apart by where each lives.</summary>
    [Fact]
    public void TwoStartupEntriesOfOneNameAreToldApartByWhereTheyLive()
    {
        var list = AgentPc.List([], [new AgentStartupEntry("Example Tool", "HKCU Run", null, true, false), new AgentStartupEntry("Example Tool", "HKLM Run", null, true, false)]);
        Assert.Equal(["Example Tool (HKCU Run)", "Example Tool (HKLM Run)"], list.ListedOthers.Select(i => i.Id));
        var plan = Plan("""{ "version": 1, "turnOff": { "startup": ["Example Tool (HKLM Run)"] } }""", list);
        Assert.Equal("HKLM Run", Assert.Single(plan.Startup).Entry.Location);
        // The bare name is on this PC twice: never matched to either, and not said to be missing.
        var bare = Assert.Single(Plan("""{ "version": 1, "turnOff": { "startup": ["example tool"] } }""", list).Refused);
        Assert.Equal(AgentAnswer.NamedDifferently, bare.Why);
        // A task named by its path where the list names it by its name alone is said the same way; a name on no item is missing.
        var tasks = AgentPc.List([], [], [new AgentScheduledTask(@"\VendorA\Updater", "Updater", null, true)]);
        Assert.Equal(AgentAnswer.NamedDifferently, Assert.Single(Plan("""{ "version": 1, "turnOff": { "tasks": ["\\VendorA\\Updater"] } }""", tasks).Refused).Why);
        Assert.Contains("not one of this PC's scheduled tasks", Assert.Single(Plan("""{ "version": 1, "turnOff": { "tasks": ["Nothing Here"] } }""", tasks).Refused).Why);
    }

    /// <summary>
    /// When two items share a name, the review says which one each change is about: a startup entry with where it lives (an
    /// entry in the user's own Run key can take the name of one every account starts), a task with its path. An item whose
    /// name no other shares is shown by its name alone.
    /// </summary>
    [Fact]
    public void TheReviewSaysWhereAnItemLivesWhenAnotherSharesItsName()
    {
        var list = AgentPc.List([],
            [
                new AgentStartupEntry("Example Tool", "HKCU Run", null, true, false),
                new AgentStartupEntry("Example Tool", "HKLM Run", null, true, false),
                new AgentStartupEntry("Example Deck", "HKCU Run", null, true, false),
            ],
            [
                new AgentScheduledTask(@"\VendorA\Updater", "Updater", null, true),
                new AgentScheduledTask(@"\VendorB\Updater", "Updater", null, false),
                new AgentScheduledTask(@"\VendorA\Cleanup", "Cleanup", null, true),
            ]);
        var plan = Plan("""
            { "version": 1,
              "turnOff": { "startup": ["Example Tool (HKLM Run)", "Example Deck"], "tasks": ["\\VendorA\\Updater", "\\VendorB\\Updater", "Cleanup"] },
              "keep": { "startup": ["example tool (hkcu run)"] } }
            """, list);
        Assert.Equal(["Example Tool (HKLM Run)", "Example Deck"], plan.Startup.Select(s => s.Item.Name));
        Assert.Equal([@"Updater (\VendorA\Updater)", "Cleanup"], plan.Tasks.Select(t => t.Item.Name));
        Assert.Equal([@"Updater (\VendorB\Updater)"], plan.AlreadyOff.Select(a => a.Name));
        Assert.Equal(["Example Tool (HKCU Run)"], plan.Kept.Select(k => k.Name));
    }

    /// <summary>
    /// The review cuts a name at 80 characters, and ids at 120, so two names can differ in the prompt and still read the same
    /// in the review. Each item that does is shown with where it lives or its path, and if that still reads the same, by its
    /// id in the prompt, which the answer's name matched alone. Names that differ only in case read the same too.
    /// </summary>
    [Fact]
    public void TheReviewNeverShowsTwoItemsAlikeWhenTheirNamesRunPastItsCut()
    {
        static string Long(string end) => "Example Vendor Background Helper " + new string('x', 52) + end; // 85 characters agree
        string cut = Long("")[..77] + "...";
        string folder = @"\Example Vendor Software " + new string('f', 60);
        var list = AgentPc.List(
            [AgentPc.Svc(Long("1"), "Example Vendor Service", ms: false), AgentPc.Svc(Long("2"), "Example Vendor Service", ms: false)],
            [
                new AgentStartupEntry(Long("A"), "HKCU Run", null, true, false),
                new AgentStartupEntry(Long("B"), "HKLM Run", null, true, false),
                new AgentStartupEntry(Long("C"), "HKCU Run", null, true, false),
                new AgentStartupEntry("Example Sync " + new string('y', 72) + "1", "HKCU Run", null, true, false),
                new AgentStartupEntry("EXAMPLE SYNC " + new string('Y', 72) + "2", "HKCU Run", null, true, false),
            ],
            [
                new AgentScheduledTask(folder + @"\A\Updater", "Updater", null, true),
                new AgentScheduledTask(folder + @"\B\Updater", "Updater", null, true),
                new AgentScheduledTask(folder + @"\A\Cleanup", "Cleanup", null, true),
            ]);
        Assert.All(list.Items, i => Assert.False(i.Kind == AgentItemKind.Startup && i.ToldApart)); // every entry's id is its own name
        var plan = Plan($$"""
            { "version": 1,
              "turnOff": { "services": ["{{Long("1")}}", "{{Long("2")}}"],
                           "startup": ["{{Long("A")}}", "{{Long("B")}}", "{{Long("C")}}", "{{"Example Sync " + new string('y', 72) + "1"}}"],
                           "tasks": ["{{(folder + @"\A\Updater").Replace(@"\", @"\\")}}", "{{(folder + @"\B\Updater").Replace(@"\", @"\\")}}", "Cleanup"] },
              "keep": { "startup": ["{{"EXAMPLE SYNC " + new string('Y', 72) + "2"}}"] } }
            """, list);
        Assert.Empty(plan.Refused);
        // Two services of one display name: by the name in the prompt.
        Assert.Equal([Long("1"), Long("2")], plan.Services.Select(s => s.Item.Name));
        // In two places, the place tells them apart; in one place, the id in the prompt does.
        Assert.Equal([Long("A"), cut + " (HKLM Run)", Long("C"), "Example Sync " + new string('y', 72) + "1"], plan.Startup.Select(s => s.Item.Name));
        Assert.Equal(["EXAMPLE SYNC " + new string('Y', 72) + "2"], plan.Kept.Select(k => k.Name));
        // Two tasks of one name whose paths agree past the cut: by the whole path, the id in the prompt.
        Assert.Equal([folder + @"\A\Updater", folder + @"\B\Updater", "Cleanup"], plan.Tasks.Select(t => t.Item.Name));

        // An entry with no place to say is shown by its name alone, never with empty brackets.
        var placeless = AgentPc.List([], [new AgentStartupEntry(Long("A"), "", null, true, false), new AgentStartupEntry(Long("B"), "HKLM Run", null, true, false)]);
        var two = Plan($$"""{ "version": 1, "turnOff": { "startup": ["{{Long("A")}}", "{{Long("B")}}"] } }""", placeless);
        Assert.Equal([cut, cut + " (HKLM Run)"], two.Startup.Select(s => s.Item.Name));
    }

    /// <summary>
    /// A name with its place can read like another item's own name, or its id like another's name with its place. The review
    /// checks its names again until none read the same: here it takes three rounds, and each entry is shown apart.
    /// </summary>
    [Fact]
    public void TheReviewChecksItsNamesAgainUntilNoneReadTheSame()
    {
        string p = "Example Helper " + new string('h', 62); // 77 characters: every name below is cut to p + "..."
        var list = AgentPc.List([],
            [
                new AgentStartupEntry(p + "zzzz", "HKLM Run", null, true, false),
                new AgentStartupEntry(p + "... (HKLM Run)", "HKCU Run", null, true, false),
                new AgentStartupEntry(p + "... (HKLM Run)X", "HKCU Run", null, true, false),
            ]);
        // Round one shows each with its place: the second and third then read the same, so round two shows them by their
        // ids, which are their names; the second's reads like the first's with its place, so round three shows the first by its id.
        var plan = Plan($$"""{ "version": 1, "turnOff": { "startup": ["{{p}}zzzz", "{{p}}... (HKLM Run)", "{{p}}... (HKLM Run)X"] } }""", list);
        Assert.Empty(plan.Refused);
        Assert.Equal([p + "zzzz", p + "... (HKLM Run)", p + "... (HKLM Run)X"], plan.Startup.Select(s => s.Item.Name));
    }

    /// <summary>
    /// A reason is text for the review, never acted on: a command, a path or an address in it stays plain text, cleaned of
    /// control and formatting characters and cut to its bound; the plan holds only items from the list.
    /// </summary>
    [Fact]
    public void AReasonIsOnlyEverPlainBoundedText()
    {
        string reason = "Run cmd /c del C:\\x then open https://example.com/x\u202E\n" + new string('y', 1000);
        var plan = Plan($$"""{ "version": 1, "turnOff": { "services": ["MapsBroker"] }, "why": { "MapsBroker": {{System.Text.Json.JsonSerializer.Serialize(reason)}} } }""");
        string kept = Assert.Single(plan.Services).Item.Reason!;
        Assert.StartsWith("Run cmd /c del C:\\x then open https://example.com/x yyy", kept);
        Assert.Equal(AgentAnswer.MaxReasonChars, kept.Length);
        Assert.EndsWith("...", kept);
        Assert.DoesNotContain('\u202E', kept);
        Assert.DoesNotContain('\n', kept);
        Assert.Equal(["MapsBroker"], plan.ServiceNames);
    }

    [Fact]
    public void ThePlansServicesAreAProfileForSaving()
    {
        var plan = Plan("""{ "version": 1, "turnOff": { "services": ["MapsBroker", "ExampleVendorSvc"] } }""");
        var profile = plan.AsProfile("From my agent");
        Assert.Equal("From my agent", profile.Name);
        Assert.Equal(["MapsBroker", "ExampleVendorSvc"], profile.Services);
        // A profile keeps only services the catalogue lists: reading it leaves the vendor's service out and says so.
        var set = ServiceProfileRules.Clean(new ServiceProfileSet { Profiles = [profile] }, AgentPc.Cat);
        Assert.Equal(["MapsBroker"], set.Profiles.Single().Services);
        Assert.Contains("ExampleVendorSvc ignored", Assert.Single(set.Problems));
    }

    [Fact]
    public void AnEmptyAnswerIsAnEmptyPlan()
    {
        var plan = Plan("""{ "version": 1 }""");
        Assert.False(plan.HasChanges);
        Assert.Empty(plan.Refused);
    }

    // ---------------------------------------------------------------- the whole answer refused

    [Fact]
    public void AnAnswerLargerThanTheBoundIsNotRead()
    {
        string big = """{ "version": 1, "why": { "MapsBroker": " """ + new string(' ', AgentAnswer.MaxBytes) + "\" } }";
        Assert.Contains("larger than 64 KB", Refused(big));
        Assert.Contains("larger than 64 KB", AgentAnswer.Read(Encoding.UTF8.GetBytes(big), AgentPc.Small()).Error);
        Assert.Contains("larger than 64 KB", AgentAnswer.Read(new byte[AgentAnswer.MaxBytes + 1], AgentPc.Small()).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"version\": 1")]
    [InlineData("{ \"version\": 1, }")]                             // a trailing comma
    [InlineData("{ /* note */ \"version\": 1 }")]                   // a comment
    [InlineData("{ \"version\": 1 } { \"version\": 1 }")]
    [InlineData("```json\n{ \"version\": 1 }\n```")]                // a markdown fence round it
    [InlineData("{ \"version\": 1, \"turnOff\": { \"services\": [[[\"x\"]]] } }")] // nested past the bound
    public void MalformedJsonIsRefused(string json) => Assert.StartsWith("The answer is not valid JSON", Refused(json));

    [Theory]
    [InlineData("[]", "one JSON object")]
    [InlineData("{ }", "\"version\" must be 1")]
    [InlineData("{ \"version\": 2 }", "\"version\" must be 1")]
    [InlineData("{ \"version\": \"1\" }", "\"version\" must be 1")]
    [InlineData("{ \"version\": 1.5 }", "\"version\" must be 1")]
    [InlineData("{ \"version\": 1, \"command\": \"x\" }", "field PoolWatch does not know: \"command\"")]
    [InlineData("{ \"version\": 1, \"TurnOff\": {} }", "field PoolWatch does not know: \"TurnOff\"")]
    [InlineData("{ \"version\": 1, \"turnOff\": { \"drivers\": [\"x\"] } }", "list PoolWatch does not know: \"drivers\"")]
    [InlineData("{ \"version\": 1, \"keep\": { \"services\": [\"a\"], \"url\": [\"x\"] } }", "list PoolWatch does not know: \"url\"")]
    [InlineData("{ \"version\": 1, \"version\": 1 }", "\"version\" twice")]
    [InlineData("{ \"version\": 1, \"turnOff\": { \"services\": [\"a\"], \"services\": [\"b\"] } }", "\"services\" twice")]
    [InlineData("{ \"version\": 1, \"turnOff\": [\"MapsBroker\"] }", "must be an object")]
    [InlineData("{ \"version\": 1, \"turnOff\": null }", "must be an object")]
    [InlineData("{ \"version\": 1, \"turnOff\": { \"services\": \"MapsBroker\" } }", "must be a list of names")]
    [InlineData("{ \"version\": 1, \"turnOff\": { \"services\": [1] } }", "only names")]
    [InlineData("{ \"version\": 1, \"turnOff\": { \"services\": [\"\"] } }", "only names")]
    [InlineData("{ \"version\": 1, \"turnOff\": { \"services\": [{ \"name\": \"x\" }] } }", "only names")]
    [InlineData("{ \"version\": 1, \"why\": [\"x\"] }", "one reason per name")]
    [InlineData("{ \"version\": 1, \"why\": { \"MapsBroker\": 5 } }", "must be text")]
    [InlineData("{ \"version\": 1, \"why\": { \"\": \"x\" } }", "not a name")]
    [InlineData("{ \"version\": 1, \"why\": { \"MapsBroker\": \"first\", \"mapsbroker\": \"second\" } }", "\"mapsbroker\" twice in \"why\"")] // names match in any case
    public void AnAnswerNotInTheDocumentedFormIsRefused(string json, string why) => Assert.Contains(why, Refused(json));

    [Fact]
    public void TooManyOrTooLongNamesAreRefused()
    {
        string many = string.Join(",", Enumerable.Range(0, AgentAnswer.MaxNamesPerList + 1).Select(i => $"\"s{i}\""));
        Assert.Contains($"more than {AgentAnswer.MaxNamesPerList} names", Refused($$"""{ "version": 1, "turnOff": { "services": [{{many}}] } }"""));
        Assert.Contains("only names", Refused($$"""{ "version": 1, "turnOff": { "services": ["{{new string('a', AgentAnswer.MaxNameChars + 1)}}"] } }"""));
        // The reasons too: at most one per name the three lists can hold, each named in 1 to 512 characters.
        string Reasons(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $"\"s{i}\":\"x\""));
        Assert.Null(AgentAnswer.Read($$"""{ "version": 1, "why": { {{Reasons(3 * AgentAnswer.MaxNamesPerList)}} } }""", AgentPc.Small()).Error);
        Assert.Contains($"more than {3 * AgentAnswer.MaxNamesPerList} reasons", Refused($$"""{ "version": 1, "why": { {{Reasons(3 * AgentAnswer.MaxNamesPerList + 1)}} } }"""));
        Assert.Null(AgentAnswer.Read($$"""{ "version": 1, "why": { "{{new string('a', AgentAnswer.MaxNameChars)}}": "x" } }""", AgentPc.Small()).Error);
        Assert.Contains("not a name of 1 to 512", Refused($$"""{ "version": 1, "why": { "{{new string('a', AgentAnswer.MaxNameChars + 1)}}": "x" } }"""));
    }

    /// <summary>
    /// JSON can write half of a surrogate pair as a \u escape, which is not text: in a name, a reason or a field's name it
    /// refuses the whole answer, read as bytes or as text, and reading never throws. So does text given with one as it is.
    /// </summary>
    [Theory]
    [InlineData("""{ "version": 1, "why": { "MapsBroker": "x\udc00" } }""")]                // in a reason
    [InlineData("""{ "version": 1, "turnOff": { "services": ["\udc00"] } }""")]            // in a name
    [InlineData("""{ "version": 1, "turnOff": { "services": ["a\ud800b"] } }""")]          // a first half with no second
    [InlineData("""{ "version": 1, "why": { "\udc00": "x" } }""")]                         // as a reason's name
    [InlineData("""{ "version": 1, "\ud800": 1 }""")]                                      // as a field's name
    public void TextThatIsNotUnicodeIsRefusedAndNeverThrows(string json)
    {
        string expected = "The answer is not valid JSON (it holds text that is not valid Unicode).";
        Assert.Equal(expected, Refused(json));
        var fromBytes = AgentAnswer.Read(Encoding.UTF8.GetBytes(json), AgentPc.Small());
        Assert.Equal((null, expected), (fromBytes.Plan, fromBytes.Error));
    }

    [Fact]
    public void TextWithHalfASurrogatePairAsItIsIsRefused()
    {
        string json = "{ \"version\": 1, \"why\": { \"MapsBroker\": \"x" + '\uD800' + "\" } }";
        Assert.Equal("The answer is not valid JSON (it holds text that is not valid Unicode).", Refused(json));
    }

    [Fact]
    public void AFieldNameInAnErrorIsShownBounded()
    {
        string name = new string('n', 500) + "\nnext";
        string error = Refused($$"""{ "version": 1, {{System.Text.Json.JsonSerializer.Serialize(name)}}: 1 }""");
        Assert.DoesNotContain('\n', error);
        Assert.True(error.Length < 200, error);
    }

    [Fact]
    public void BytesMustBeUtf8AndAByteOrderMarkIsAllowed()
    {
        Assert.Contains("not UTF-8", AgentAnswer.Read([0x7B, 0xFF, 0x7D], AgentPc.Small()).Error);
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{ "version": 1, "turnOff": { "services": ["MapsBroker"] } }""")];
        Assert.Equal(["MapsBroker"], AgentAnswer.Read(withBom, AgentPc.Small()).Plan!.ServiceNames);
        Assert.Equal(["MapsBroker"], Plan("\uFEFF{ \"version\": 1, \"turnOff\": { \"services\": [\"MapsBroker\"] } }").ServiceNames);
    }
}
