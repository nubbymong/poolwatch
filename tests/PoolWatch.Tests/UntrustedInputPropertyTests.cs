using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;
using PoolWatch.Core;
using PoolWatch.Core.Changes;
using PoolWatch.Core.Knowledge;
using PoolWatch.Core.Links;
using PoolWatch.Core.Model;
using PoolWatch.Core.Services;
using PoolWatch.Core.Services.Agent;
using PoolWatch.Core.Setup;
using PoolWatch.Core.Updates;
using PoolWatch.Core.Util;
using static PoolWatch.Tests.PropertyInputs;

namespace PoolWatch.Tests;

/// <summary>
/// Property tests (FsCheck) of the code that reads what PoolWatch does not write itself, or reads as untrusted although it
/// does, while it runs as administrator: the knowledge packs, the services catalogue, version tags and release pages,
/// applied-changes.json, service-changes.json, an AI agent's answer, Startup-folder shortcuts, settings.json, the
/// retention clock's state and the cached scan of the PC. Each reader is given hundreds of random inputs, most of them a
/// good file changed at random (<see cref="PropertyInputs"/>), and each property says what must hold for every one of
/// them: reading never throws but in the one way the reader documents, and whatever is read is within the bounds and
/// past the gates that the code acting on it relies on. Where a property says more than "never throws", it is checked
/// here again without the reader's code.
/// </summary>
public class UntrustedInputPropertyTests
{
    /// <summary>Services the protected gate always keeps, in other cases too: Windows security and Update, core Windows, Hyper-V's integration services.</summary>
    private static readonly string[] Protected = ["WinDefend", "windefend", "WUAUSERV", "RpcSs", "mpssvc", "TrustedInstaller", "BITS", "vmicheartbeat", "VMICvss"];

    private static readonly string[] ProtectedJson = [.. Protected.Select(n => JsonSerializer.Serialize(n)), "\"wisvc\"", "[\"WinDefend\"]"];

    /// <summary>Pieces of the names a user or a vendor gives things: no control character, and letters that look like others.</summary>
    private static readonly string[] NamePieces =
        ["a", "Z", "q", "0", "7", " ", "-", "_", ".", "(", ")", "'", "&", ",", "\u00E9", "\u00DF", "\u0130", "\u212A", "\u4E2D", "\U0001F600"];

    /// <summary>A name of 1 to 24 pieces.</summary>
    private static Gen<string> Names => Gen.Choose(1, 24).SelectMany(n => Gen.ArrayOf(Gen.Elements(NamePieces), n)).Select(p => string.Concat(p));

    private static readonly DateTime Start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Gen<DateTime> Times => Gen.Choose(0, int.MaxValue - 1).Select(s => Start.AddTicks(s * 7919L));

    private static string Resource(Assembly assembly, string name)
    {
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    // ------------------------------------------------------------------ knowledge packs

    private static readonly string[] Packs = typeof(KnowledgeBase).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("knowledge/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        .Select(n => Resource(typeof(KnowledgeBase).Assembly, n)).ToArray();

    private static readonly string[] PackValues =
    [
        "\"Reff\"", "\"Ab \"", "\"abcde\"", "\"\\u00e9ab\"", "\"auto-clear\"", "\"ReFS\"", "\"NTFS\"", "\"a-b\"", "\"A-b\"", "\"-a\"",
        "\"https://github.com/x\"", "\"http://github.com/x\"", "\"https://github.com.example.org/x\"", "\"https://user@github.com/x\"",
        "\"https://github.com:8443/x\"", "\"https://GitHub.com/x\"", "\"https://learn.microsoft.com/a b\"", "\"https://github.com/\\\"x\"",
        "\"file:///C:/x\"", "1073741824", "1073741825", "99999", "100000", "[\"Reff\",\"Reff\"]",
    ];

    private static readonly string[] LinkSites = ["github.com", "learn.microsoft.com", "support.microsoft.com"];

    [Fact]
    public void AKnowledgePackIsUsedOnlyWithinTheLimitsItsFormatSets()
    {
        Assert.NotEmpty(Packs);
        Assert.All(Packs, p => Assert.Null(KnowledgeBase.Parse(p).Problem)); // the changes start from packs that are used
        var packs = Gen.OneOf(Mutated(Gen.Elements(Packs), PackValues), AnyText);
        ForAll(500, packs, json =>
        {
            var (issue, problem) = KnowledgeBase.Parse(json);
            Assert.True((issue is null) != (problem is null), "a pack is used, or left out with why");
            if (issue is not null)
            {
                Strict(json);
                CheckPack(issue);
            }
            var kb = KnowledgeBase.Load([("pack.json", json)]);
            Assert.Equal(issue is null ? 0 : 1, kb.Entries.Count);
            Assert.Equal(issue is null ? 1 : 0, kb.Problems.Count);
        });
    }

    /// <summary>
    /// The JSON docs/knowledge-packs.md asks of a pack: strict (no comments and no trailing commas, which JsonDocument's
    /// default options refuse), no field given twice in one object, nested at most 8 deep.
    /// </summary>
    private static void Strict(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.InRange(Depth(doc.RootElement), 1, 8);
    }

    /// <summary>How deep the objects and lists go, the outermost counted as 1; checks each object's field names on the way.</summary>
    private static int Depth(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var names = e.EnumerateObject().Select(p => p.Name).ToList();
                Assert.True(names.Count == names.Distinct(StringComparer.Ordinal).Count(), "a field given twice: " + string.Join(", ", names));
                return 1 + e.EnumerateObject().Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max();
            case JsonValueKind.Array:
                return 1 + e.EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max();
            default:
                return 0;
        }
    }

    /// <summary>What docs/knowledge-packs.md says of each field of a pack PoolWatch uses.</summary>
    private static void CheckPack(KnownIssue issue)
    {
        Assert.Matches(@"^[a-z0-9]+(-[a-z0-9]+)*\z", issue.Id);
        Assert.InRange(issue.Id.Length, 1, 64);
        Words(issue.Title, 60);
        Words(issue.Explanation, 600);
        Words(issue.Workaround, 400);
        if (issue.Suggestion is { } suggestion) Words(suggestion, 160);
        Assert.InRange(issue.Evidence.Count, 1, 8);
        foreach (var rule in issue.Evidence)
        {
            Assert.True(rule.TagGrowth is not null || rule.ClearFreed is not null, "a rule needs a measured growth or a clear");
            if (rule.TagGrowth is { } growth)
            {
                Tags(growth.Tags);
                Words(growth.Name, 40);
                Assert.True(double.IsFinite(growth.MinMBPerHour) && growth.MinMBPerHour > 0);
            }
            if (rule.ClearFreed is { } freed)
            {
                Tags(freed.Tags);
                Words(freed.Name, 40);
                Assert.True(double.IsFinite(freed.MinMB) && freed.MinMB > 0);
            }
        }
        Assert.InRange(issue.Links.Count, 1, 8);
        foreach (var link in issue.Links)
        {
            Words(link.Title, 100);
            Site(link.Url);
        }
        Assert.InRange(issue.Credits.Count, 0, 8);
        foreach (var credit in issue.Credits)
        {
            Words(credit.Name, 40);
            Words(credit.What, 200);
            if (credit.Url is { } url) Site(url);
        }
    }

    /// <summary>1 to 16 different pool tags, each 1 to 4 printable ASCII characters.</summary>
    private static void Tags(IReadOnlyList<string> tags)
    {
        Assert.InRange(tags.Count, 1, 16);
        Assert.All(tags, t => Assert.True(t is { Length: >= 1 and <= 4 } && t.All(c => c is >= '!' and <= '~') && PoolTag.TryParse(t, out _), t));
        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>One line of plain text: 1 to <paramref name="max"/> characters, no space at either end, no control, formatting or line-breaking character.</summary>
    private static void Words(string text, int max)
    {
        Assert.InRange(text.Length, 1, max);
        Assert.Equal(text.Trim(), text);
        Assert.DoesNotContain(text, c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
    }

    /// <summary>
    /// An https address on a site a pack may link to, written plainly: no user name, no port, and only the characters
    /// docs/knowledge-packs.md allows (letters, digits and - . _ ~ : / ? # = &amp; % +), so nothing a command line splits on.
    /// </summary>
    private static void Site(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        Assert.Equal("https", uri.Scheme);
        Assert.Contains(uri.Host, LinkSites);
        Assert.StartsWith("https://" + uri.Host + "/", url);
        Assert.True(uri.IsDefaultPort && uri.UserInfo.Length == 0, url);
        Assert.All(url, c => Assert.True(char.IsAsciiLetterOrDigit(c) || "-._~:/?#=&%+".Contains(c), url));
    }

    // ------------------------------------------------------------------ the services catalogue

    private static readonly string CatalogueJson = Resource(typeof(ServiceCatalogue).Assembly, "services-catalogue.json");

    [Fact]
    public void ACatalogueThatLoadsNeverOffersAProtectedService()
    {
        var catalogues = Mutated(Gen.Constant(CatalogueJson),
            [.. ProtectedJson, "[\"vmicvss\", null]", "{\"WinDefend\":\"manual\"}", "[\"wisvc\"]", "\"Automatic\"", "\"manual\"", "{\"x\":null}"]);
        ForAll(300, catalogues, json =>
        {
            ServiceCatalogue catalogue;
            try { catalogue = ServiceCatalogue.Load(json); }
            catch (JsonException) { return; } // refused whole: the unit tests name the row that does not load
            // The texts a row shows: the Services tab and the presets read them (ServicePlan.Tick reads the category).
            Assert.All(catalogue.Entries, e => Assert.True(e.Name is not null && e.Category is not null && e.Lose is not null, string.Join(", ", e.Services)));
            foreach (bool insider in new[] { false, true })
            {
                foreach (string name in Protected.Concat(insider ? new[] { "wisvc", "WISVC" } : Array.Empty<string>()))
                    foreach (bool microsoft in new[] { false, true })
                        Assert.Equal(ServiceGroup.Protected, catalogue.GroupOf(name, microsoft, insider));
                foreach (string name in catalogue.Entries.SelectMany(e => e.Services))
                {
                    Assert.NotNull(catalogue.Find(name));
                    Assert.NotEqual(ServiceGroup.Unknown, catalogue.GroupOf(name, false, insider));
                    Assert.Contains(catalogue.WindowsDefault(name), new ServiceStartType?[] { null, ServiceStartType.Automatic, ServiceStartType.Manual });
                }
            }
        });
    }

    /// <summary>
    /// JSON null where a value goes. The property found the first four: a null row or list of services failed with a null
    /// reference on loading, a null service name with a null key on loading, and a null Windows default at a lookup after
    /// it. The last three, found in review, put null in a row's texts, which the code that reads a row does not expect:
    /// ServicePlan.Tick, for one, fails on a null category.
    /// </summary>
    [Theory]
    [InlineData("[null]")]
    [InlineData("""[{ "services": null, "name": "x", "category": "y" }]""")]
    [InlineData("""[{ "services": ["ExampleSvc", null], "name": "x", "category": "y" }]""")]
    [InlineData("""[{ "services": ["ExampleSvc"], "name": "x", "category": "y", "windowsDefault": { "ExampleSvc": null } }]""")]
    [InlineData("""[{ "services": ["ExampleSvc"], "name": null, "category": "y" }]""")]
    [InlineData("""[{ "services": ["ExampleSvc"], "name": "x", "category": null }]""")]
    [InlineData("""[{ "services": ["ExampleSvc"], "name": "x", "category": "y", "lose": null }]""")]
    public void ACatalogueWithNullWhereAValueGoesIsRefusedWhole(string json) => Assert.Throws<JsonException>(() => ServiceCatalogue.Load(json));

    // ------------------------------------------------------------------ version tags and release pages

    private static Gen<int> Numbers => Gen.OneOf(Gen.Choose(0, 20), Gen.Elements(0, 1, 9, 10, 99, 100, int.MaxValue - 1, int.MaxValue), Gen.Choose(0, int.MaxValue - 1));

    private static Gen<string> Identifiers => Gen.OneOf(
        Gen.Choose(0, 30).Select(n => n.ToString(CultureInfo.InvariantCulture)),
        Gen.Elements("0", "1", "2", "10", "11", "99999999999999999999", "100000000000000000000"),
        Gen.Elements("rc", "beta", "alpha", "a", "b", "-", "x-y", "0a", "a0", "RC", "Rc", "z", "Z", "01a"));

    private static Gen<SemVersion> Versions =>
        from major in Numbers
        from minor in Numbers
        from patch in Numbers
        from pre in Gen.Choose(0, 4).SelectMany(n => Gen.ListOf(Identifiers, n))
        select new SemVersion(major, minor, patch, string.Join(".", pre));

    /// <summary>Versions close to each other, so that two often share their numbers and the start of their pre-release.</summary>
    private static Gen<SemVersion> CloseVersions =>
        from major in Gen.Choose(0, 1)
        from minor in Gen.Choose(0, 1)
        from patch in Gen.Choose(0, 1)
        from pre in Gen.Choose(0, 3).SelectMany(n => Gen.ListOf(Gen.Elements("0", "1", "2", "10", "rc", "a", "b", "-", "1a", "99999999999999999999"), n))
        select new SemVersion(major, minor, patch, string.Join(".", pre));

    [Fact]
    public void AVersionReadsBackAsItIsWritten()
    {
        var cases =
            from v in Versions
            from prefix in Gen.Elements("", "v", "V")
            from build in Gen.Elements("", "+78b1fec", "+build.1", "+0")
            select (v, text: prefix + v + build);
        ForAll(500, cases, c =>
        {
            bool read = SemVersion.TryParse(c.text, out var back);
            Assert.Equal(c.text.Length <= SemVersion.MaxLength, read);
            if (read) Assert.Equal(c.v, back);
            Assert.Equal(c.v.PreRelease.Length > 0, c.v.IsPreRelease);
            string page = ReleasePage.For("v" + c.v);
            Assert.Equal(page.Length <= ReleasePage.MaxLength, ReleasePage.IsValid(page));
        });
    }

    [Fact]
    public void AVersionIsReadOnlyFromTextThatIsExactlyOne()
    {
        var texts = Gen.OneOf(AnyText, Edited(Versions.Select(v => "v" + v)), Edited(Gen.Elements("1.0.0", "v1.2.3-rc.1+abc", "V10.20.30-alpha.1.x-y")));
        ForAll(1000, texts, text =>
        {
            if (!SemVersion.TryParse(text, out var v)) return;
            Assert.InRange(text.Length, 1, SemVersion.MaxLength);
            string core = text[0] is 'v' or 'V' ? text[1..] : text;
            int plus = core.IndexOf('+');
            Assert.Equal(plus < 0 ? core : core[..plus], v.ToString()); // the text names exactly this version
            // and what follows "+" is build metadata as semver.org writes it: dot-separated identifiers, none empty.
            if (plus >= 0) Assert.Matches(@"^[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*\z", core[(plus + 1)..]);
            Assert.True(SemVersion.TryParse(v.ToString(), out var again) && again == v);
            Assert.DoesNotContain(text, c => c > '~' || char.IsWhiteSpace(c));
        });
    }

    [Fact]
    public void VersionsAreInTheOrderSemVerGives()
    {
        var pairs = from a in CloseVersions from b in Gen.OneOf(CloseVersions, Versions) select (a, b);
        ForAll(1000, pairs, p =>
        {
            int order = Math.Sign(p.a.CompareTo(p.b));
            Assert.Equal(Precedence(p.a, p.b), order);
            Assert.Equal(-order, Math.Sign(p.b.CompareTo(p.a)));
            Assert.Equal(p.a == p.b, order == 0);
        });
    }

    /// <summary>semver.org's precedence, written again: the numbers, then a release after its pre-releases, then each identifier, numbers by value (here as big integers).</summary>
    private static int Precedence(SemVersion a, SemVersion b)
    {
        int c = a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor != b.Minor ? a.Minor.CompareTo(b.Minor) : a.Patch.CompareTo(b.Patch);
        if (c != 0) return Math.Sign(c);
        if (a.PreRelease == b.PreRelease) return 0;
        if (a.PreRelease.Length == 0) return 1;
        if (b.PreRelease.Length == 0) return -1;
        string[] x = a.PreRelease.Split('.'), y = b.PreRelease.Split('.');
        for (int i = 0; i < x.Length && i < y.Length; i++)
        {
            bool xNumber = BigInteger.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xValue);
            bool yNumber = BigInteger.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yValue);
            c = (xNumber, yNumber) switch
            {
                (true, true) => xValue.CompareTo(yValue),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x[i], y[i]),
            };
            if (c != 0) return Math.Sign(c);
        }
        return Math.Sign(x.Length.CompareTo(y.Length));
    }

    [Fact]
    public void AReleasePageIsOnlyEverOneOfPoolWatchsReleases()
    {
        string tagPage = ReleasePage.For("");
        var urls = Gen.OneOf(AnyText, Edited(Gen.Elements(tagPage + "v1.2.0", tagPage + "v1.0.0-rc.1", ReleasePage.Prefix, ReleasePage.Prefix + "download/v1.0.0/PoolWatch.exe")));
        ForAll(1000, urls, url =>
        {
            if (!ReleasePage.IsValid(url)) return;
            Assert.StartsWith(tagPage, url);
            Assert.InRange(url.Length, tagPage.Length + 1, ReleasePage.MaxLength);
            string tag = url[tagPage.Length..];
            Assert.DoesNotContain("..", tag);
            Assert.All(tag, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_', url));
        });
    }

    // ------------------------------------------------------------------ applied-changes.json

    private static Gen<ChangeRecord> ChangeRecords =>
        from kind in Gen.Elements(Enum.GetValues<RecordKind>())
        from changed in Times
        from via in Names
        from name in Names
        from folder in Names
        from enabled in Gen.Elements(true, false)
        from logon in Gen.Choose(0, 6)
        from account in Gen.Elements<string?>(null, "EXAMPLE\\someone", "SYSTEM")
        from location in Gen.Elements(StartupLocation.All.Select(l => l.Label).ToArray())
        from hash in Gen.ArrayOf(Gen.Elements("0123456789abcdefABCDEF".ToCharArray()), 64)
        select Made(kind, changed, via, name, folder, enabled, logon, account, location, new string(hash));

    /// <summary>
    /// A record as PoolWatch writes one of its kind: its id from its target, a task outside Microsoft's folder, a startup
    /// place PoolWatch knows, a shortcut on a local drive, a PATH entry with a plain folder in its place, one of the
    /// variables PoolWatch sets.
    /// </summary>
    private static ChangeRecord Made(RecordKind kind, DateTime when, string via, string name, string folder, bool on, int logon, string? account, string location, string hash)
    {
        string task = @"\Example " + name, path = @"\Example " + folder + task, shortcut = @"C:\Example\" + name + ".lnk";
        return kind switch
        {
            RecordKind.RemovedClearTask => new ChangeRecord
            {
                Id = ChangeIds.RemovedClearTask(task), Kind = kind, ChangedUtc = when, Via = via, Name = task, TaskXml = "<Task>" + name + "</Task>",
                TaskLogonType = logon, TaskUserId = account,
            },
            RecordKind.ScheduledTask => new ChangeRecord
            {
                Id = ChangeIds.ScheduledTask(path), Kind = kind, ChangedUtc = when, Via = via, Location = path, Name = on ? name : null, EnabledBefore = on,
            },
            RecordKind.StartupEntry => new ChangeRecord
            {
                Id = ChangeIds.StartupEntry(location, name), Kind = kind, ChangedUtc = when, Via = via, Location = location, Name = name, EnabledBefore = on,
            },
            RecordKind.PathEntry => new ChangeRecord
            {
                Id = ChangeIds.PathEntry(on ? EnvScopes.User : EnvScopes.Machine, @"%LOCALAPPDATA%\Example " + name, when), Kind = kind, ChangedUtc = when,
                Via = via, Location = on ? EnvScopes.User : EnvScopes.Machine, Before = @"%LOCALAPPDATA%\Example " + name, After = @"C:\Example\" + folder, Index = logon,
            },
            RecordKind.EnvironmentVariable => new ChangeRecord
            {
                Id = ChangeIds.EnvironmentVariable(LinkFinder.Variables[logon % LinkFinder.Variables.Count]), Kind = kind, ChangedUtc = when, Via = via,
                Location = EnvScopes.User, Name = LinkFinder.Variables[logon % LinkFinder.Variables.Count], Before = on ? @"C:\Old\" + name : null,
                ValueKind = on ? (logon % 2 == 0 ? "String" : "ExpandString") : null, After = @"C:\Example\" + folder,
            },
            _ => new ChangeRecord { Id = ChangeIds.Shortcut(shortcut), Kind = kind, ChangedUtc = when, Via = via, Path = shortcut, Sha256 = hash },
        };
    }

    /// <summary>Records with different ids, in any case: the file keeps one record for each target.</summary>
    private static Gen<List<ChangeRecord>> ChangeLists => Gen.ListOf(ChangeRecords).Select(OnePerTarget);

    /// <summary>More records than a file is read for (<see cref="ChangeRecordFile.MaxRecords"/>), so the property reaches that bound.</summary>
    private static Gen<List<ChangeRecord>> TooManyChanges =>
        Gen.Choose(ChangeRecordFile.MaxRecords + 20, ChangeRecordFile.MaxRecords + 60).SelectMany(n => Gen.ListOf(ChangeRecords, n)).Select(OnePerTarget);

    private static List<ChangeRecord> OnePerTarget(List<ChangeRecord> records) => records.DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();

    private static readonly string[] ChangeValues =
    [
        "\"RemovedClearTask\"", "\"Shortcut\"", "\"shortcut\"", "\"StartupEntry\"", "\"\\\\Microsoft\\\\Windows\\\\x\"", "\"\\\\a/b\"", "\"\\\\..\"",
        "\"\\\\\"", "\"HKCU Run\"", "\"hkcu run\"", "\"C:\\\\x.LNK\"", "\"\\\\\\\\server\\\\x.lnk\"", "\"service:x\"", "7", "-1",
    ];

    [Fact]
    public void ChangeRecordsReadBackAsTheyAreSaved()
    {
        ForAll(300, ChangeLists, records =>
        {
            Assert.All(records, r => Assert.Null(ChangeRecordFile.Invalid(r))); // the generator makes only records PoolWatch writes
            Assert.Equal(records, ChangeRecordFile.Parse(ChangeRecordFile.ToJson(records)));
        });
    }

    [Fact]
    public void AChangedRecordsFileIsReadOnlyAsFarAsItHoldsValidRecords()
    {
        var files = Gen.OneOf(
            Mutated(Gen.OneOf(ChangeLists, TooManyChanges).Select(r => ChangeRecordFile.ToJson(r)), ChangeValues),
            JustOver(ChangeLists.Select(r => ChangeRecordFile.ToJson(r)), ChangeRecordFile.MaxBytes),
            AnyText);
        ForAll(500, files, json =>
        {
            var read = ChangeRecordFile.Parse(json);
            if (Encoding.UTF8.GetByteCount(json) > ChangeRecordFile.MaxBytes) Assert.Null(read); // too large: not read at all
            if (read is not { } records) return; // not read: never written over
            Assert.InRange(records.Count, 0, ChangeRecordFile.MaxRecords);
            Assert.All(records, r => Assert.Null(ChangeRecordFile.Invalid(r)));
            Assert.Equal(records.Count, records.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    /// <summary>
    /// Every entry of a changed file's list is read or kept as its text, never left out, and a save of what was read
    /// (its records, then the entries kept) loses nothing: read again, it gives the same records and keeps the same
    /// entries, each exactly as it was.
    /// </summary>
    [Fact]
    public void AChangedRecordsFileLosesNoEntryWhenItIsSavedAgain()
    {
        var files = Gen.OneOf(Mutated(Gen.OneOf(ChangeLists, TooManyChanges).Select(r => ChangeRecordFile.ToJson(r)), ChangeValues), AnyText);
        ForAll(500, files, json =>
        {
            if (ChangeRecordFile.Parse(json, out IReadOnlyList<string> kept) is not { } records) return; // not read: never written over
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }))
                Assert.Equal(doc.RootElement.GetProperty("changes").GetArrayLength(), records.Count + kept.Count);
            string saved = ChangeRecordFile.ToJson(records, kept);
            if (Encoding.UTF8.GetByteCount(saved) > ChangeRecordFile.MaxBytes) return; // a save is refused then, and the file stays as it is
            Assert.Equal(records, ChangeRecordFile.Parse(saved, out IReadOnlyList<string> again));
            Assert.Equal(kept, again);
        });
    }

    /// <summary>Cases the property found: reading the version threw, as JsonElement.TryGetInt32 does on another kind.</summary>
    [Theory]
    [InlineData("""{ "version": "1", "changes": [] }""")]
    [InlineData("""{ "version": null, "changes": [] }""")]
    [InlineData("""{ "version": [1], "changes": [] }""")]
    public void AChangesFileWhoseVersionIsNotANumberIsNotRead(string json) => Assert.Null(ChangeRecordFile.Parse(json));

    // ------------------------------------------------------------------ service-changes.json

    private static Gen<ServiceChange> ServiceChanges =>
        from service in Names
        from display in Gen.OneOf(Names, Gen.Constant(""))
        from original in Gen.Elements(Enum.GetValues<ServiceStartType>())
        from changedTo in Gen.Elements(Enum.GetValues<ServiceStartType>())
        from running in Gen.Elements(true, false)
        from notKnown in Gen.Elements(true, false)
        from changed in Times
        from via in Gen.OneOf(Names, Gen.Constant("manual"))
        from cores in Gen.Elements<double?>(null, 0, 0.25, 1.5, 1e-9, 12345.678)
        from faults in Gen.Elements<long?>(null, 0, 7, long.MaxValue)
        from pid in Gen.Elements<int?>(null, 0, 4242)
        from underWay in Gen.Elements<ChangeUnderWay?>(null, new ChangeUnderWay { Pid = 4242, ProcessStarted = 133_000_000_000_000_000, TickMs = 5000 },
            new ChangeUnderWay { Pid = 1, ProcessStarted = 1, TickMs = 1, PutBack = true })
        select new ServiceChange
        {
            Service = service, DisplayName = display, OriginalStart = original, NewStart = changedTo, OriginalRunning = running, OriginalRunningNotKnown = notKnown,
            ChangedUtc = changed, Via = via, BeforeCores = cores, BeforePageFaultsPerSec = faults, NotStoppedPid = pid, UnderWay = underWay,
        };

    [Fact]
    public void ServiceChangesReadBackAsTheyAreSaved()
    {
        ForAll(300, Gen.ListOf(ServiceChanges), changes =>
        {
            Assert.Equal(changes, ServiceChangeLog.Parse(ServiceChangeLog.ToJson(changes)));
        });
    }

    [Fact]
    public void AChangedServiceChangesFileIsReadWholeOrNotAtAll()
    {
        var files = Gen.OneOf(Mutated(Gen.ListOf(ServiceChanges).Select(c => ServiceChangeLog.ToJson(c)), "\"Disabled\"", "\"disabled\"", "9", "-2"), AnyText);
        ForAll(500, files, json =>
        {
            if (ServiceChangeLog.Parse(json) is not { } changes) return; // not read: never written over
            // Read whole: a record for each item of the file's list, none of them dropped.
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(doc.RootElement.GetArrayLength(), changes.Count);
            Assert.All(changes, c =>
            {
                Assert.NotNull(c);
                Assert.False(string.IsNullOrEmpty(c.Service));
            });
        });
    }

    // ------------------------------------------------------------------ an AI agent's answer

    /// <summary>The made-up PC of the agent tests, enrolled in Insider builds, with the Insider service and a driver on it as well.</summary>
    private static readonly AgentItemList AgentList = AgentPc.Small(insider: true,
        more: [AgentPc.Svc("wisvc", "Windows Insider Service"), AgentPc.Svc("ExampleFilter", "Example Filter Driver", win32: false)]);

    private static string[] AgentIds(AgentItemKind kind, params string[] more) =>
        [.. AgentList.Items.Where(i => i.Kind == kind).Select(i => i.Id), .. AgentList.Items.Where(i => i.Kind == kind).Select(i => i.Id.ToUpperInvariant()), .. more];

    /// <summary>Answers in the documented form, naming items on the list, protected services, a driver and names that are not on the PC.</summary>
    private static Gen<string> AgentAnswers
    {
        get
        {
            string[] services = AgentIds(AgentItemKind.Service, "WinDefend", "wisvc", "ExampleFilter", "NotOnThisPc");
            string[] startup = AgentIds(AgentItemKind.Startup, "Not There");
            string[] tasks = AgentIds(AgentItemKind.Task, @"\Not There");
            return
                from offServices in Gen.SubListOf(services)
                from offStartup in Gen.SubListOf(startup)
                from offTasks in Gen.SubListOf(tasks)
                from keep in Gen.SubListOf(services)
                from why in Gen.SubListOf(services.Concat(startup).ToArray())
                select JsonSerializer.Serialize(new
                {
                    version = 1,
                    turnOff = new { services = offServices, startup = offStartup, tasks = offTasks },
                    keep = new { services = keep },
                    why = why.DistinctBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(n => n, n => "You said you never use " + n + "."),
                });
        }
    }

    [Fact]
    public void AnAgentAnswerOnlyEverPlansWhatIsOnThePcAndPassesTheGate()
    {
        string[] values = [.. ProtectedJson, "\"ExampleFilter\"", "[\"WinDefend\",\"windefend\"]", "{\"services\":[\"WinDefend\"]}", "\"" + new string('x', 513) + "\"", "2"];
        var answers = Gen.OneOf(AsBytes(Mutated(AgentAnswers, values)), AsBytes(AgentAnswers),
            JustOver(AgentAnswers, AgentAnswer.MaxBytes).Select(t => Encoding.UTF8.GetBytes(t)), AnyBytes);
        var services = AgentList.Items.Where(i => i.Service is not null).Select(i => i.Service!.Service.Name).ToList();
        var startup = AgentList.Items.Where(i => i.Startup is not null).Select(i => i.Startup!).ToList();
        var tasks = AgentList.Items.Where(i => i.Task is not null).Select(i => i.Task!).ToList();
        ForAll(500, answers, bytes =>
        {
            var result = AgentAnswer.Read(bytes, AgentList);
            Assert.True((result.Plan is null) != (result.Error is null), "an answer is read into a plan, or refused with why");
            if (bytes.Length > AgentAnswer.MaxBytes) Assert.Null(result.Plan); // too large: refused unread
            if (result.Plan is not { } plan) return;
            foreach (var planned in plan.Services)
            {
                string name = planned.Option.Service.Name;
                Assert.Contains(name, services);
                Assert.False(ServiceCatalogue.IsProtected(name, insiderEnrolled: true), name);
                Assert.DoesNotContain(name, Protected, StringComparer.OrdinalIgnoreCase);
                Assert.True(planned.Option.Service.IsWin32, name); // never a driver
            }
            Assert.All(plan.Startup, p => Assert.Contains(p.Entry, startup));
            Assert.All(plan.Tasks, p => Assert.Contains(p.Task, tasks));
            // Nothing is planned twice.
            Assert.Equal(plan.Services.Count, plan.Services.Select(s => s.Option.Service.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(plan.Startup.Count, plan.Startup.Select(p => p.Entry).Distinct().Count());
            Assert.Equal(plan.Tasks.Count, plan.Tasks.Select(p => p.Task).Distinct().Count());
        });
    }

    // ------------------------------------------------------------------ Startup-folder shortcuts

    /// <summary>
    /// A minimal shortcut in the [MS-SHLLINK] layout: the header (shown normally), an optional item list of one item and its
    /// terminator, LinkInfo with the VolumeID of a fixed drive, a local base path and an empty suffix, and optional arguments.
    /// </summary>
    private static byte[] Lnk(string target, string? args, bool idList)
    {
        var ms = new MemoryStream();
        var header = new byte[0x4C];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        new Guid("00021401-0000-0000-C000-000000000046").ToByteArray().CopyTo(header, 4);
        uint flags = 0x2 | 0x80 | (args is null ? 0u : 0x20u) | (idList ? 0x1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), 1); // ShowCommand: SW_SHOWNORMAL
        ms.Write(header);
        if (idList)
        {
            ms.Write([8, 0]);             // IDListSize: the item and the terminator
            ms.Write([6, 0, 1, 2, 3, 4]); // one item: its size, then its data
            ms.Write([0, 0]);             // TerminalID
        }
        var volumeId = new byte[0x11]; // VolumeIDSize, DriveType, DriveSerialNumber, VolumeLabelOffset, then an empty label
        BinaryPrimitives.WriteUInt32LittleEndian(volumeId, (uint)volumeId.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(volumeId.AsSpan(4), 3); // DRIVE_FIXED
        BinaryPrimitives.WriteUInt32LittleEndian(volumeId.AsSpan(8), 0x1234ABCD);
        BinaryPrimitives.WriteUInt32LittleEndian(volumeId.AsSpan(0xC), 0x10);
        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        int volumeAt = 0x1C, baseAt = volumeAt + volumeId.Length, suffixAt = baseAt + basePath.Length, size = suffixAt + 1;
        var li = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(li, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(4), 0x1C); // LinkInfoHeaderSize: no Unicode offsets
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(8), 1); // VolumeIDAndLocalBasePath
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(0xC), (uint)volumeAt);
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(0x10), (uint)baseAt);
        BinaryPrimitives.WriteUInt32LittleEndian(li.AsSpan(0x18), (uint)suffixAt); // an empty suffix: its terminating zero
        volumeId.CopyTo(li, volumeAt);
        basePath.CopyTo(li, baseAt);
        ms.Write(li);
        if (args is not null)
        {
            ms.Write(BitConverter.GetBytes((ushort)args.Length));
            ms.Write(Encoding.Unicode.GetBytes(args));
        }
        return ms.ToArray();
    }

    /// <summary>Such shortcuts: a target of 1 to 40 single-byte characters, arguments or none, an item list or none.</summary>
    private static Gen<(string Target, string? Args, byte[] Bytes)> Shortcuts =>
        from target in Gen.Choose(1, 40).SelectMany(n => Gen.ArrayOf(Gen.Choose(1, 255).Select(c => (char)c), n)).Select(c => new string(c))
        from args in Gen.OneOf(Gen.Constant<string?>(null), Names.Select(n => (string?)n))
        from idList in Gen.Elements(true, false)
        select (target, args, Lnk(target, args, idList));

    [Fact]
    public void AShortcutReadsBackAsItIsWritten()
    {
        ForAll(500, Shortcuts, s =>
        {
            Assert.Equal<(string, string?)?>((s.Target, s.Args), ShellLink.Read(s.Bytes));
        });
    }

    [Fact]
    public void AnyBytesInAStartupFolderAreReadSafely()
    {
        var files = Gen.OneOf(AnyBytes, Shortcuts.SelectMany(s => Damaged(s.Bytes)));
        ForAll(2000, files, bytes =>
        {
            if (ShellLink.Read(bytes) is not { } link) return;
            Assert.NotEmpty(link.Target);
            Assert.True(link.Arguments is null || link.Arguments.Length > 0);
        });
    }

    // ------------------------------------------------------------------ settings.json

    /// <summary>Settings with something in each list and path, and a profile applied at start.</summary>
    private static string RichSettings()
    {
        var offered = DefaultCatalogue.Shared.Entries.SelectMany(e => e.Services).Where(s => !ServiceCatalogue.IsProtected(s, insiderEnrolled: true)).Take(3).ToList();
        var settings = new Settings
        {
            PoolTagTxtPath = @"D:\Tools\pooltag.txt", AiccResourcesRoot = @"D:\Resources", SymbolPath = @"srv*D:\Symbols*https://msdl.microsoft.com/download/symbols",
            WatchedApps = ["Example.exe", "Other App"], GrowthRules = [new(5, 1024), new(60, 1L << 30)], MarkHotkey = "Ctrl+Shift+M",
            ServiceProfiles = new ServiceProfileSet { Profiles = [new ServiceProfile("Quiet", offered), new ServiceProfile("Empty", [])], AtStart = "Quiet", DelayMinutes = 5 },
        };
        return settings.ToJson();
    }

    private static readonly string[] SettingNumbers = ["0", "-1", "1", "4", "5", "29", "30", "60", "90", "91", "120", "300", "301", "3600", "100000", "0.5", "1e308", "2147483647", "2147483648"];

    private static readonly string[] SettingTexts =
    [
        "\"\"", "\" \"", "\"D:\\\\x\"", "\" D:\\\\x \"", "\"D:/x\"", "\"C:x\"", "\"x\"", "\"\\\\\\\\server\\\\share\"", "\"\\\\\\\\?\\\\C:\\\\x\"",
        "\"C:\\\\a:b\"", "\"C:\\\\*\"", "\"C:\\\\x\\n\"", "\"auto\"", "\"standby\"", "\"lowpriority\"", "\"STANDBY\"", "\"Detailed\"",
        "\"NeedsAttention\"", "\"off\"", "\"Ctrl+Alt+M\"", "\"\\ud800\"", "\"C:\\\\" + new string('a', 300) + "\"", "\"" + new string('a', 5000) + "\"",
    ];

    private static readonly string[] SettingLists =
    [
        "[]", "[null]", "[\"\"]", "[\" a \",\"A\",\"a\"]", "[\"x\\\\y\",\"ok.exe\",\"c:d\"]", "[{\"windowMinutes\":0,\"bytes\":1}]",
        "[{\"windowMinutes\":120,\"bytes\":5},{\"windowMinutes\":1e-9,\"bytes\":1}]", "[{\"windowMinutes\":-1,\"bytes\":-1}]", "[null,{}]",
        "[" + string.Join(",", Enumerable.Range(0, 150).Select(i => "\"app" + i + "\"")) + "]",
    ];

    private static string[] ProfileSets
    {
        get
        {
            string offered = DefaultCatalogue.Shared.Entries.SelectMany(e => e.Services).First(s => !ServiceCatalogue.IsProtected(s, insiderEnrolled: true));
            return
            [
                "{}", "{\"profiles\":[]}", "{\"profiles\":null}", "{\"atStart\":5}", "{\"delayMinutes\":1e400}",
                "{\"profiles\":[{\"name\":\"p\",\"services\":[\"WinDefend\",\"wisvc\",\"" + offered + "\",\"" + offered.ToUpperInvariant() + "\"]}],\"atStart\":\" p \",\"delayMinutes\":5}",
                "{\"profiles\":[{\"name\":\"\\ud800\",\"services\":[]}]}", "{\"profiles\":[{\"name\":\"p\",\"services\":[\"x\"]},{\"name\":\"P \",\"services\":[]}],\"atStart\":\"P\",\"delayMinutes\":0}",
                "{\"profiles\":[{\"name\":\"Telemetry only\",\"services\":[]}]}", "{\"profiles\":[{\"name\":\" q \",\"services\":[\"" + offered + "\", \" " + offered + "\"]}]}",
            ];
        }
    }

    /// <summary>
    /// The default settings with 1 to 8 of them given another value: null often, else mostly one of its kind at an edge, and
    /// sometimes a value of another kind.
    /// </summary>
    private static Gen<string> SettingsTexts
    {
        get
        {
            var defaults = JsonNode.Parse(new Settings().ToJson())!.AsObject();
            string[] names = [.. defaults.Select(p => p.Key)];
            string[] profiles = ProfileSets;
            return
                from picks in Gen.Choose(1, 8).SelectMany(n => Gen.ArrayOf(Gen.Elements(names), n))
                from r in Gen.Choose(0, int.MaxValue - 1)
                select SettingsWith(defaults, picks, profiles, new Random(r));
        }
    }

    private static readonly string[] SettingFlags = ["true", "false", "0", "\"true\""];

    private static string[] Pick(Random rng, params string[][] pools) => pools[rng.Next(pools.Length)];

    private static string SettingsWith(JsonObject defaults, string[] picks, string[] profiles, Random rng)
    {
        var o = defaults.DeepClone().AsObject();
        var values = new List<string>();
        foreach (string name in picks)
        {
            var kind = o[name]?.GetValueKind() ?? JsonValueKind.Null;
            string[] pool = kind switch
            {
                JsonValueKind.Number => SettingNumbers,
                JsonValueKind.String => SettingTexts,
                JsonValueKind.Array => SettingLists,
                JsonValueKind.Object => profiles,
                _ => SettingFlags,
            };
            if (rng.Next(5) == 0) pool = Pick(rng, SettingNumbers, SettingTexts, SettingLists, profiles, SettingFlags); // a value of another kind
            values.Add(rng.Next(4) == 0 ? "null" : pool[rng.Next(pool.Length)]);
            o[name] = JsonValue.Create("@@value" + (values.Count - 1) + "@@");
        }
        string text = o.ToJsonString();
        for (int v = 0; v < values.Count; v++) text = text.Replace("\"@@value" + v + "@@\"", values[v], StringComparison.Ordinal);
        return text;
    }

    [Fact]
    public void SettingsAreReadWithinTheirBoundsAndReadBackAsTheyAreSaved()
    {
        var texts = Gen.OneOf(SettingsTexts, Mutated(Gen.Elements(new Settings().ToJson(), RichSettings()), [.. SettingTexts, .. SettingLists]), AnyText);
        ForAll(500, texts, json =>
        {
            foreach (bool explicitConfig in new[] { false, true })
            {
                Settings settings;
                try { settings = Settings.Parse(json, explicitConfig); }
                catch (JsonException) { continue; } // not settings PoolWatch can read: refused whole
                WithinBounds(settings);
                string saved = settings.ToJson();
                Assert.Equal(saved, Settings.Parse(saved, explicitConfig).ToJson()); // saved, then read again: nothing changes
            }
        });
    }

    /// <summary>The case the property found: a null path read as no pooltag.txt, and as the default path once saved (null is left out).</summary>
    [Fact]
    public void ANullPoolTagPathReadsAsNoneAndStaysSoWhenSaved()
    {
        var settings = Settings.Parse("""{ "poolTagTxtPath": null }""");
        Assert.Equal("", settings.PoolTagTxtPath);
        Assert.Equal("", Settings.Parse(settings.ToJson()).PoolTagTxtPath);
    }

    /// <summary>
    /// Another case the property found: a number too large for a double (1e400, or -1e400) reads as an infinity, so each
    /// setting with no range of its own reads as its default then, and the settings save and read back.
    /// </summary>
    [Theory]
    [InlineData(nameof(Settings.VolumeIntervalMinutes))]
    [InlineData(nameof(Settings.HandleScanBudgetMs))]
    [InlineData(nameof(Settings.RetentionMinutes))]
    [InlineData(nameof(Settings.ReleaseWindowMinutes))]
    [InlineData(nameof(Settings.IncidentQuietMinutes))]
    [InlineData(nameof(Settings.IncidentMaxMinutes))]
    [InlineData(nameof(Settings.TranscriptWindowMinutes))]
    [InlineData(nameof(Settings.TagCsvIntervalSeconds))]
    [InlineData(nameof(Settings.ProcessCsvIntervalSeconds))]
    public void ANumberTooLargeForADoubleReadsAsTheSettingsDefault(string name)
    {
        var property = typeof(Settings).GetProperty(name)!;
        double expected = (double)property.GetValue(Settings.Parse("{}"))!;
        foreach (string big in new[] { "1e400", "-1e400" })
        {
            var settings = Settings.Parse($$"""{ "{{JsonNamingPolicy.CamelCase.ConvertName(name)}}": {{big}} }""");
            Assert.Equal(expected, (double)property.GetValue(settings)!);
            Assert.Equal(settings.ToJson(), Settings.Parse(settings.ToJson()).ToJson());
        }
    }

    /// <summary>
    /// The settings that had no range: a huge time (1e308) made a TimeSpan that overflows, and 0 or less read the drives
    /// at every tick or closed every incident at once. Each is now kept within the range its description gives, a value
    /// far outside it reads as the nearest end, and the ends themselves are kept. The two CSV intervals start at the
    /// sample interval (10 seconds by default).
    /// </summary>
    [Theory]
    [InlineData(nameof(Settings.VolumeIntervalMinutes), 1.0, 1440.0)]
    [InlineData(nameof(Settings.HandleScanBudgetMs), 10.0, 60000.0)]
    [InlineData(nameof(Settings.RetentionMinutes), 0.5, 600.0)]
    [InlineData(nameof(Settings.ReleaseWindowMinutes), 1.0, 60.0)]
    [InlineData(nameof(Settings.IncidentQuietMinutes), 0.5, 600.0)]
    [InlineData(nameof(Settings.IncidentMaxMinutes), 10.0, 1440.0)]
    [InlineData(nameof(Settings.TranscriptWindowMinutes), 1.0, 1440.0)]
    [InlineData(nameof(Settings.TagCsvIntervalSeconds), 10.0, 3600.0)]
    [InlineData(nameof(Settings.ProcessCsvIntervalSeconds), 10.0, 3600.0)]
    public void ASettingThatHadNoRangeIsReadWithinItsRange(string name, double lo, double hi)
    {
        var property = typeof(Settings).GetProperty(name)!;
        double Read(string value) => (double)property.GetValue(Settings.Parse($$"""{ "{{JsonNamingPolicy.CamelCase.ConvertName(name)}}": {{value}} }"""))!;
        Assert.Equal(hi, Read("1e308"));
        Assert.Equal(lo, Read("-5"));
        Assert.Equal(lo, Read("0"));
        Assert.Equal(lo, Read(lo.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(hi, Read(hi.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The settings that kept NaN: the Advanced settings' grid reads typed text with .NET's number converter, which takes
    /// "NaN", and Math.Clamp gives NaN back as it is, so the setting stayed NaN and the next save failed (JSON holds no
    /// NaN). Each now reads as its default, and so does an infinity (which the clamp alone sent to the nearer end), and
    /// the settings save. The probe's interval keeps its own rule that 0 turns it off.
    /// </summary>
    [Theory]
    [InlineData(nameof(Settings.SampleIntervalSeconds), 1.0, 300.0)]
    [InlineData(nameof(Settings.HandleIntervalSeconds), 5.0, 3600.0)]
    [InlineData(nameof(Settings.ServiceIntervalSeconds), 5.0, 3600.0)]
    [InlineData(nameof(Settings.SpawnStormWindowSeconds), 5.0, 600.0)]
    [InlineData(nameof(Settings.ShortLivedSeconds), 0.1, 600.0)]
    [InlineData(nameof(Settings.StrayGitCheckMinutes), 1.0, 1440.0)]
    [InlineData(nameof(Settings.TokenBucketSeconds), 1.0, 3600.0)]
    [InlineData(nameof(Settings.LagProbeSeconds), 2.0, 600.0)]
    [InlineData(nameof(Settings.LagAlertRatio), 1.5, 1000.0)]
    [InlineData(nameof(Settings.TokenWindowMinutes), 1.0, 1440.0)]
    public void ASettingThatIsNotAFiniteNumberReadsAsItsDefault(string name, double lo, double hi)
    {
        var property = typeof(Settings).GetProperty(name)!;
        double expected = (double)property.GetValue(new Settings())!;
        foreach (double notFinite in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var settings = new Settings();
            property.SetValue(settings, notFinite);
            settings.Normalize();
            double value = (double)property.GetValue(settings)!;
            Assert.InRange(value, lo, hi);
            Assert.Equal(expected, value);
            string saved = settings.ToJson(); // throws on a NaN or an infinity
            Assert.Equal(expected, (double)property.GetValue(Settings.Parse(saved, explicitConfig: true))!);
        }
        if (name == nameof(Settings.LagProbeSeconds))
        {
            var off = new Settings { LagProbeSeconds = -1 };
            off.Normalize();
            Assert.Equal(0.0, off.LagProbeSeconds); // still off
        }
    }

    /// <summary>
    /// Every setting that is a floating-point number (a double, a float or a Half, or a Nullable one), not only those
    /// above: set to NaN or an infinity of its own type, alone or all at once, it reads as its default, and the settings
    /// save. A growth alert's window that is NaN drops the alert, and an infinite one is cut to 60 minutes. With the next
    /// test, which finds no other setting that holds a floating-point number, no setting a later build adds can keep a
    /// number that is not finite unseen.
    /// </summary>
    [Fact]
    public void NoSettingKeepsANumberThatIsNotFinite()
    {
        var defaults = new Settings();
        var numbers = FloatingPointSettings();
        Assert.True(numbers.Count >= 30, $"only {numbers.Count} settings are floating-point numbers");
        foreach (int which in new[] { 0, 1, 2 }) // NaN, plus infinity, minus infinity
        {
            double window = (double)NotFinite(typeof(double), which);
            var all = new Settings { GrowthRules = [new(double.NaN, 5), new(window, 7)] };
            foreach (var p in numbers)
            {
                object notFinite = NotFinite(p.PropertyType, which);
                var one = new Settings();
                p.SetValue(one, notFinite);
                p.SetValue(all, notFinite);
                one.Normalize();
                Assert.True(Equals(p.GetValue(defaults), p.GetValue(one)), $"{p.Name} set to {notFinite} is {p.GetValue(one)}, not its default {p.GetValue(defaults)}");
                _ = one.ToJson();
            }
            all.Normalize();
            foreach (var p in numbers) Assert.True(Equals(p.GetValue(defaults), p.GetValue(all)), $"{p.Name} with every number set to {NotFinite(p.PropertyType, which)} is {p.GetValue(all)}");
            (double, long)[] rules = double.IsPositiveInfinity(window) ? [(60.0, 7L)] : [];
            Assert.Equal(rules, all.GrowthRules.Select(r => (r.WindowMinutes, r.Bytes)));
            _ = all.ToJson();
        }
    }

    /// <summary>
    /// The settings the test above sets are the only ones that are a floating-point number, and the growth alerts the only
    /// other setting that holds one. One held anywhere else (a setting that cannot be set, or a number in a list, an
    /// array, a dictionary or a property of a type of its own, at any depth) would go unchecked, so it fails this test.
    /// </summary>
    [Fact]
    public void TheGrowthAlertsAreTheOnlyOtherSettingThatHoldsAFloatingPointNumber()
    {
        // The look finds one wherever it can be held, and only there.
        Assert.True(HoldsFloatingPoint(typeof(List<GrowthRule>)) && HoldsFloatingPoint(typeof(Half?[])) && HoldsFloatingPoint(typeof(Dictionary<string, float>)));
        Assert.False(HoldsFloatingPoint(typeof(ServiceProfileSet)) || HoldsFloatingPoint(typeof(TimeSpan)) || HoldsFloatingPoint(typeof(List<string>)));

        var numbers = FloatingPointSettings().Select(p => p.Name).ToHashSet();
        var others = typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !numbers.Contains(p.Name) && p.Name != nameof(Settings.GrowthRules) && HoldsFloatingPoint(p.PropertyType)).Select(p => p.Name).ToList();
        Assert.Empty(others);
    }

    /// <summary>Every setting that is a floating-point number, or a Nullable one, and can be set.</summary>
    private static List<PropertyInfo> FloatingPointSettings() =>
        typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => FloatingPoint.Contains(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) && p.CanWrite).ToList();

    /// <summary>The floating-point types: each has NaN and the two infinities.</summary>
    private static readonly Type[] FloatingPoint = [typeof(double), typeof(float), typeof(Half)];

    /// <summary>NaN (0), plus infinity (1) or minus infinity (2), as a value of <paramref name="type"/> (of the type it holds, for a Nullable one).</summary>
    private static object NotFinite(Type type, int which)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(float)) return new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }[which];
        if (type == typeof(Half)) return new[] { Half.NaN, Half.PositiveInfinity, Half.NegativeInfinity }[which];
        return new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }[which];
    }

    /// <summary>
    /// Whether a value of <paramref name="type"/> can hold a floating-point number: it is one (or a Nullable one), or one
    /// is held by what it holds (an array's, a list's or a dictionary's elements: .NET's own types are looked into only
    /// through those) or by a public property of a type of its own, at any depth.
    /// </summary>
    private static bool HoldsFloatingPoint(Type type) => HoldsFloatingPoint(type, []);

    private static bool HoldsFloatingPoint(Type type, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (FloatingPoint.Contains(type)) return true;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || !seen.Add(type)) return false;
        if (type.IsArray) return HoldsFloatingPoint(type.GetElementType()!, seen);
        if (type.GetGenericArguments().Any(a => HoldsFloatingPoint(a, seen))) return true;
        if (type.Namespace is "System" || type.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true) return false;
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => p.GetIndexParameters().Length == 0 && HoldsFloatingPoint(p.PropertyType, seen));
    }

    /// <summary>The bounds the settings' descriptions give, which the overhead budget and the elevated app's reads rely on.</summary>
    private static void WithinBounds(Settings s)
    {
        Assert.InRange(s.SampleIntervalSeconds, 1, 300);
        Assert.InRange(s.HandleIntervalSeconds, 5, 3600);
        Assert.InRange(s.VolumeIntervalMinutes, 1, 24 * 60);
        Assert.InRange(s.HandleScanBudgetMs, 10, 60_000);
        Assert.InRange(s.RetentionMinutes, 0.5, 600);
        Assert.InRange(s.ReleaseWindowMinutes, 1, 60);
        Assert.InRange(s.IncidentQuietMinutes, 0.5, 600);
        Assert.InRange(s.IncidentMaxMinutes, 10, 24 * 60);
        Assert.InRange(s.TranscriptWindowMinutes, 1, 24 * 60);
        Assert.InRange(s.TagCsvIntervalSeconds, s.SampleIntervalSeconds, 3600);
        Assert.InRange(s.ProcessCsvIntervalSeconds, s.SampleIntervalSeconds, 3600);
        Assert.InRange(s.RetentionDays, 1, 90);
        Assert.InRange(s.CaptureSeconds, 5, Settings.CaptureHardCapSeconds);
        Assert.InRange(s.AutoClearMinMinutes, 30, 24 * 60);
        Assert.Contains(s.AutoClearMode, new[] { "auto", "lowpriority", "standby" });
        Assert.True(Enum.IsDefined(s.PoolNotices) && Enum.IsDefined(s.RecordMode));
        Assert.True(s.TagCsvIntervalSeconds >= s.SampleIntervalSeconds && s.ProcessCsvIntervalSeconds >= s.SampleIntervalSeconds);
        Assert.All(s.GrowthRules, r => Assert.True(r is not null && r.WindowMinutes > 0 && r.WindowMinutes <= 60 && r.Bytes > 0));
        Assert.InRange(s.WatchedApps.Count, 0, 100);
        Assert.All(s.WatchedApps, a =>
        {
            Assert.InRange(a.Length, 1, 64);
            Assert.Equal(a.Trim(), a);
            Assert.DoesNotContain(a, c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c));
        });
        Assert.Equal(s.WatchedApps.Count, s.WatchedApps.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string? path in new[] { s.AiccResourcesRoot, s.ClaudeVersionsDir, s.PoolTagTxtPath })
            Assert.True(string.IsNullOrEmpty(path) || Settings.IsLocalDrivePath(path), path);
        Assert.InRange(s.SymbolPath.Length, 0, Settings.MaxSymbolPathLength);
        Assert.DoesNotContain(s.SymbolPath, char.IsControl);
        Assert.InRange(s.MarkHotkey.Length, 0, 40);
        foreach (var p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(double)))
            Assert.True(double.IsFinite((double)p.GetValue(s)!), p.Name);
        foreach (var profile in s.ServiceProfiles.Profiles)
            Assert.All(profile.Services, service =>
            {
                Assert.NotNull(DefaultCatalogue.Shared.Find(service));
                Assert.DoesNotContain(service, Protected, StringComparer.OrdinalIgnoreCase);
            });
        Assert.InRange(s.ServiceProfiles.DelayMinutes, 1, 15);
        Assert.Equal(Settings.CurrentSettingsVersion, s.SettingsVersion);
    }

    // ------------------------------------------------------------------ the retention clock's state

    private const long MaxStateMs = long.MaxValue / 4; // RetentionClock's bound on a time it reads

    private static Gen<RetentionClock.State> RetentionStates =>
        from counted in Gen.Choose(0, int.MaxValue - 1).Select(c => c * 1000L)
        from saved in Gen.Choose(0, int.MaxValue - 1).Select(c => (long)c)
        from items in Gen.ListOf(
            from name in Names
            from stamp in Gen.Choose(-5, int.MaxValue - 1)
            from part in Gen.Choose(0, 1000)
            select (name, kept: new RetentionClock.Kept(stamp, counted * part / 1000)))
        select new RetentionClock.State(saved, counted,
            items.DistinctBy(i => i.name, StringComparer.OrdinalIgnoreCase).ToDictionary(i => i.name, i => i.kept, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void TheRetentionClocksStateReadsBackAsItIsSaved()
    {
        ForAll(300, RetentionStates, state =>
        {
            var back = RetentionClock.State.Parse(state.ToJson())!;
            Assert.NotNull(back);
            Assert.Equal((state.SavedMs, state.CountedMs), (back.SavedMs, back.CountedMs));
            Assert.Equal(state.Items.OrderBy(i => i.Key, StringComparer.Ordinal), back.Items.OrderBy(i => i.Key, StringComparer.Ordinal));
        });
    }

    [Fact]
    public void AChangedRetentionStateIsReadOnlyWhenEveryTimeInItCanBe()
    {
        var files = Gen.OneOf(Mutated(RetentionStates.Select(s => s.ToJson()), "2305843009213693951", "2305843009213693952", "\"Name\"", "\"NAME\""), AnyText);
        ForAll(500, files, json =>
        {
            if (RetentionClock.State.Parse(json) is not { } state) return;
            Assert.InRange(state.SavedMs, 0, MaxStateMs);
            Assert.InRange(state.CountedMs, 0, MaxStateMs);
            Assert.All(state.Items, i =>
            {
                Assert.NotEmpty(i.Key);
                Assert.InRange(i.Value.FirstSeenMs, 0, state.CountedMs);
            });
        });
    }

    /// <summary>
    /// Cases the property found: JsonObject refused the field written twice with an ArgumentException, which was not caught.
    /// The state is now parsed with a field given twice refused, as a JsonException. A name written once as it is and once
    /// with an escape is the same name twice, and is refused too.
    /// </summary>
    [Theory]
    [InlineData("""{ "savedMs": 1, "savedMs": 2, "countedMs": 5, "items": [] }""")]
    [InlineData("""{ "savedMs": 1, "countedMs": 5, "items": [{ "name": "a", "stamp": 1, "firstSeenMs": 1, "firstSeenMs": 2 }] }""")]
    [InlineData("""{ "savedMs": 1, "\u0073avedMs": 2, "countedMs": 5, "items": [] }""")] // the same name, once with an escape
    public void ARetentionStateWithAFieldWrittenTwiceIsNotRead(string json) => Assert.Null(RetentionClock.State.Parse(json));

    // ------------------------------------------------------------------ the cached scan of the PC

    [Fact]
    public void ACachedScanIsReadOnlyAsAScanCanMakeIt()
    {
        string[] values =
        [
            "\"C:\\\\\"", "\"c:\\\\\"", "\"C:\"", "\"C:/\"", "\"\\\\\\\\?\\\\C:\\\\\"", "\"1:\\\\\"", "\"\\u0130:\\\\\"", "\"NTFS\"", "\"ReFS \"",
            "\"FAT-32\"", "\"x64\"", "\"ARM64\"", "\"arm64\"", "\"Starts\"", "\"starts\"", "\"Starts, TaskOff\"",
        ];
        // And a scan with more of each list than a scan keeps (32 of each, 5 names of running services), so the property
        // reaches those bounds.
        var full = EnvironmentFactsTests.Sample() with
        {
            Volumes = [.. Enumerable.Range(0, 40).Select(i => new VolumeFact($"{(char)('A' + i % 26)}:\\", "NTFS", false, null))],
            UnreadVolumes = [.. Enumerable.Range(0, 40).Select(i => $"{(char)('A' + i % 26)}:\\")],
            AiTools = [.. Enumerable.Range(0, 40).Select(i => new ToolFact("Example tool " + i, "1." + i))],
            OwnClearTasks = [.. Enumerable.Range(0, 40).Select(i => @"\Example clear " + i)],
            OptionalServiceExamples = [.. Enumerable.Range(0, 40).Select(i => "Example Service " + i)],
        };
        var files = Gen.OneOf(Mutated(Gen.Elements(EnvironmentFactsTests.Sample().ToJson(), full.ToJson()), values), AnyText);
        ForAll(500, files, json =>
        {
            if (EnvironmentFacts.Parse(json) is not { } facts) return;
            Assert.Contains(facts.Architecture, new[] { "x64", "ARM64" });
            // The roots go into the commands the Defender advice asks the user to paste into PowerShell.
            Assert.All(facts.Volumes, v =>
            {
                Assert.Matches(@"^[A-Za-z]:\\\z", v.Root);
                Assert.Matches(@"^[A-Za-z0-9]{1,16}\z", v.FileSystem);
            });
            Assert.All(facts.UnreadVolumes, root => Assert.Matches(@"^[A-Za-z]:\\\z", root));
            var texts = new[] { facts.WindowsName, facts.DisplayVersion, facts.Build, facts.InsiderChannel }
                .Concat(facts.AiTools.SelectMany(t => new[] { t.Name, t.Version })).Concat(facts.OwnClearTasks).Concat(facts.OptionalServiceExamples);
            Assert.All(texts, t => Assert.True(t is null || t.Length <= 200, t));
            Assert.All(new[] { facts.Volumes.Count, facts.UnreadVolumes.Count, facts.AiTools.Count, facts.OwnClearTasks.Count }, n => Assert.InRange(n, 0, 32));
            Assert.InRange(facts.OptionalServiceExamples.Count, 0, 5);
            Assert.True(facts.OptionalServicesRunning >= 0);
            Assert.NotEmpty(facts.PlainLines());
        });
    }
}
