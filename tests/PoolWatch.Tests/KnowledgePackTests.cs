using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PoolWatch.Core.Knowledge;

namespace PoolWatch.Tests;

/// <summary>
/// The knowledge packs: the shipped ones are built in and load cleanly, the schema contributors edit against says what
/// the reader checks, and a malformed or hostile pack is left out with a reason, without throwing and without affecting
/// the other packs, while fields a newer pack adds are ignored.
/// </summary>
public class KnowledgePackTests
{
    /// <summary>The repository's knowledge folder (the test runs from bin/ inside the checkout).</summary>
    private static string KnowledgeDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PoolWatch.slnx"))) return Path.Combine(dir.FullName, "knowledge");
        throw new InvalidOperationException("PoolWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    private static List<string> PackFiles() =>
        Directory.GetFiles(KnowledgeDir(), "*.json").Where(f => Path.GetFileName(f) != "schema.json").Order(StringComparer.Ordinal).ToList();

    /// <summary>A small pack that passes every check; each test breaks one thing in it.</summary>
    private static JsonObject Valid() => JsonNode.Parse("""
        {
          "format": 1,
          "id": "test-issue",
          "title": "A test issue",
          "explanation": "What is going on.",
          "workaround": "What helps.",
          "actions": ["auto-clear"],
          "suggestion": "An hourly clear keeps it down.",
          "evidence": [ { "tagGrowth": { "tags": ["Abcd"], "name": "test records", "minMBPerHour": 10 }, "fileSystems": ["ReFS"] } ],
          "links": [ { "title": "A report", "url": "https://github.com/example/repo/issues/1" } ],
          "credits": [ { "name": "someone", "what": "found it", "url": "https://github.com/someone" } ]
        }
        """)!.AsObject();

    private static string? Problem(string json)
    {
        var (issue, problem) = KnowledgeBase.Parse(json);
        Assert.True(issue is null == problem is not null);
        return problem;
    }

    // ------------------------------------------------------------------ the shipped packs

    [Fact]
    public void TheShippedPacksAreBuiltInAndLoadCleanly()
    {
        var shipped = KnowledgeBase.Embedded();
        Assert.Empty(shipped.Problems);
        Assert.Equal(["codex-gpu-shader-cache", "ntfc-ai-desktop-apps", "refs-standby-metadata", "windows-token-leak"], shipped.Entries.Select(e => e.Id));
        // Every pack file in the repository is built in: one added without the build picking it up fails here.
        Assert.Equal(PackFiles().Select(Path.GetFileNameWithoutExtension), shipped.Entries.Select(e => e.Id));
        Assert.Equal(["MSPi", "MSb+", "MSeb", "NtFC", "RLin", "RefF", "Reff", "Toke"], shipped.GrowthTags.Select(t => t.ToDisplay()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EachShippedPackIsNamedAfterItsIdAndWrittenInPlainWords()
    {
        var camelCase = new Regex(@"\b[a-z]+[A-Z]\w*");
        foreach (var file in PackFiles())
        {
            var (issue, problem) = KnowledgeBase.Parse(File.ReadAllText(file));
            Assert.True(problem is null, $"{file}: {problem}");
            Assert.Equal(Path.GetFileNameWithoutExtension(file), issue!.Id);
            var texts = new[] { issue.Title, issue.Explanation, issue.Workaround, issue.Suggestion ?? "" }
                .Concat(issue.Evidence.SelectMany(r => new[] { r.TagGrowth?.Name, r.ClearFreed?.Name }.Concat(r.Running?.Select(p => p.Name) ?? [])).OfType<string>());
            foreach (var text in texts)
            {
                Assert.DoesNotContain("!", text);
                Assert.False(camelCase.IsMatch(text), $"{issue.Id}: \"{text}\" reads like a setting name");
            }
        }
    }

    [Fact]
    public void EachShippedPackLinksItsSourcesAndCreditsTheirAuthors()
    {
        var byId = KnowledgeBase.Embedded().Entries.ToDictionary(e => e.Id);
        static List<string> Urls(KnownIssue i) => i.Links.Select(l => l.Url).ToList();

        var token = byId["windows-token-leak"];
        Assert.Equal(["https://github.com/bentoner/windows-token-leak", "https://github.com/gunrgbcr/windows-token-leak-logon-fix"], Urls(token));
        Assert.Equal(["bentoner", "gunrgbcr"], token.Credits.Select(c => c.Name));
        Assert.Contains("no code was copied", token.Credits[1].What); // gunrgbcr declares no licence: the idea only
        Assert.Equal(["token-guard"], token.Actions);
        // Wherever the guard is explained: what you will notice, that it is set again each time PoolWatch starts (Windows resets
        // it at each sign-in, and PoolWatch may start at other times), and how to stop it.
        Assert.Contains("take your keystrokes, even while you type", token.Workaround);
        Assert.Contains("rare and easy to live with", token.Workaround);
        Assert.Contains("turn it off in Active changes", token.Workaround);
        Assert.Contains("sets it again each time PoolWatch starts", token.Workaround);
        Assert.Contains("each time PoolWatch starts", token.Suggestion); // the card keeps it once the pack gives the guide's row its reason
        Assert.DoesNotContain("sign-in", token.Suggestion);

        var ntfc = byId["ntfc-ai-desktop-apps"];
        Assert.Equal(["https://github.com/openai/codex/issues/56160", "https://github.com/anthropics/claude-code/issues/55361",
            "https://github.com/openai/codex/issues/31212"], Urls(ntfc));
        Assert.Empty(ntfc.Actions);
        // Shown beside the shader-cache note for the Codex app, which offers something worth trying: not "cannot".
        Assert.StartsWith("PoolWatch has no sure fix for this", ntfc.Workaround);

        var shader = byId["codex-gpu-shader-cache"];
        Assert.Equal("https://github.com/openai/codex/issues/31212#issuecomment-5635622083", shader.Links[0].Url);
        Assert.Equal(["Faarfarin"], shader.Credits.Select(c => c.Name));
        Assert.Equal(["chatgpt-shortcut"], shader.Actions);

        var refs = byId["refs-standby-metadata"];
        Assert.Equal(["https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/fix-heavy-memory-usage-refs",
            "https://github.com/microsoft/Windows-Dev-Performance/issues/131"], Urls(refs));
        Assert.Equal(["auto-clear"], refs.Actions);
    }

    [Fact]
    public void ThePacksLinksAreThePagesANoteMayOpen()
    {
        var shipped = KnowledgeBase.Embedded();
        Assert.True(shipped.HasLink("https://github.com/openai/codex/issues/56160"));
        Assert.True(shipped.HasLink("https://github.com/openai/codex/issues/31212#issuecomment-5635622083")); // a credit's page
        Assert.False(shipped.HasLink("https://github.com/openai/codex/issues/1"));
        Assert.False(shipped.HasLink("https://github.com/openai/codex/issues/56160 x"));
    }

    // ------------------------------------------------------------------ the schema says what the reader checks

    [Fact]
    public void TheSchemaAgreesWithTheReader()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(KnowledgeDir(), "schema.json")));
        var root = doc.RootElement;
        var defs = root.GetProperty("$defs");
        static List<string> Names(JsonElement properties) => properties.EnumerateObject().Select(p => p.Name).Where(n => n is not ("$schema" or "format")).Order().ToList();
        static List<string> Fields(Type t) => t.GetProperties().Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name).Order().ToList();
        static List<string> Required(Type t) => t.GetProperties().Where(p => p.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name).Order().ToList();
        static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();
        var props = root.GetProperty("properties");
        var rule = defs.GetProperty("rule").GetProperty("properties");

        Assert.Equal(Fields(typeof(KnownIssue)), Names(props));
        Assert.Equal(Required(typeof(KnownIssue)), root.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Where(n => n != "format").Order());
        Assert.Equal(Fields(typeof(EvidenceRule)), Names(rule));
        Assert.Equal(Fields(typeof(TagGrowthRule)), Names(rule.GetProperty("tagGrowth").GetProperty("properties")));
        Assert.Equal(Fields(typeof(ClearFreedRule)), Names(rule.GetProperty("clearFreed").GetProperty("properties")));
        Assert.Equal(Fields(typeof(ProcessRule)), Names(rule.GetProperty("running").GetProperty("items").GetProperty("properties")));
        Assert.Equal(Fields(typeof(BuildRange)), Names(rule.GetProperty("windowsBuild").GetProperty("properties")));
        Assert.Equal(Fields(typeof(KnownIssueLink)), Names(props.GetProperty("links").GetProperty("items").GetProperty("properties")));
        Assert.Equal(Fields(typeof(KnownIssueCredit)), Names(props.GetProperty("credits").GetProperty("items").GetProperty("properties")));

        Assert.Equal(KnownActions.All.Select(a => a.Id), props.GetProperty("actions").GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["any", "nonPaged", "paged"], rule.GetProperty("tagGrowth").GetProperty("properties").GetProperty("pool").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(KnowledgeBase.Format, Int(props.GetProperty("format"), "const"));
        Assert.Equal(KnowledgeBase.MaxId, Int(props.GetProperty("id"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxTitle, Int(props.GetProperty("title"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxExplanation, Int(props.GetProperty("explanation"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxWorkaround, Int(props.GetProperty("workaround"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxSuggestion, Int(props.GetProperty("suggestion"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxActions, Int(props.GetProperty("actions"), "maxItems"));
        Assert.Equal(KnowledgeBase.MaxRules, Int(props.GetProperty("evidence"), "maxItems"));
        Assert.Equal(KnowledgeBase.MaxLinks, Int(props.GetProperty("links"), "maxItems"));
        Assert.Equal(KnowledgeBase.MaxCredits, Int(props.GetProperty("credits"), "maxItems"));
        Assert.Equal(KnowledgeBase.MaxLinkTitle, Int(props.GetProperty("links").GetProperty("items").GetProperty("properties").GetProperty("title"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxName, Int(props.GetProperty("credits").GetProperty("items").GetProperty("properties").GetProperty("name"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxCreditWhat, Int(props.GetProperty("credits").GetProperty("items").GetProperty("properties").GetProperty("what"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxName, Int(defs.GetProperty("name"), "maxLength"));
        Assert.Equal(KnowledgeBase.MaxTags, Int(defs.GetProperty("tags"), "maxItems"));
        Assert.Equal(KnowledgeBase.MaxPrograms, Int(rule.GetProperty("running"), "maxItems"));
        Assert.Equal(KnowledgeBase.MaxFileSystems, Int(rule.GetProperty("fileSystems"), "maxItems"));
        Assert.Equal(KnowledgeBase.MaxMB, defs.GetProperty("amount").GetProperty("maximum").GetDouble());
        Assert.Equal(KnowledgeBase.MaxUrl, Int(defs.GetProperty("url"), "maxLength"));
        string urlPattern = defs.GetProperty("url").GetProperty("pattern").GetString()!;
        Assert.Equal(KnowledgeBase.LinkHosts.Order(), Regex.Match(urlPattern, @"^\^https://\((?<h>[^)]*)\)").Groups["h"].Value.Split('|').Select(h => h.Replace(@"\.", ".")).Order());
    }

    // ------------------------------------------------------------------ malformed packs are left out, with a reason, never a throw

    [Fact]
    public void TheValidTestPackLoads()
    {
        var (issue, problem) = KnowledgeBase.Parse(Valid().ToJsonString());
        Assert.Null(problem);
        Assert.Equal("test-issue", issue!.Id);
        Assert.Equal(PoolKind.Any, issue.Evidence[0].TagGrowth!.Pool); // the default
    }

    [Theory]
    [InlineData("format 2", "\"format\" must be 1")]
    [InlineData("format as text", "\"format\" must be 1")]
    [InlineData("format 1.5", "\"format\" must be 1")]
    [InlineData("no format", "\"format\" must be 1")]
    [InlineData("no title", "title")]
    [InlineData("title null", "title")]
    [InlineData("title a number", "title")]
    [InlineData("title too long", "title: 1 to 60 characters")]
    [InlineData("title blank", "title: 1 to 60 characters")]
    [InlineData("title with a space before it", "title: no spaces")]
    [InlineData("a line break", "explanation: no control")]
    [InlineData("a control character", "title: no control")]
    [InlineData("a right-to-left override", "workaround: no control or invisible")]
    [InlineData("a zero-width space", "title: no control or invisible")]
    [InlineData("a line separator", "title: no control")]
    [InlineData("a paragraph separator", "title: no control")]
    [InlineData("id in capitals", "id:")]
    [InlineData("id with a path", "id:")]
    [InlineData("an id with a line break at the end", "id:")]
    [InlineData("an unknown action", "actions: only the built-in")]
    [InlineData("a command as the action", "actions: only the built-in")]
    [InlineData("an action twice", "actions: at most 3, each once")]
    [InlineData("an action without its suggestion", "suggestion: 1 to 160")]
    [InlineData("a suggestion without an action", "suggestion: shown only")]
    [InlineData("no evidence", "evidence: 1 to 8")]
    [InlineData("an empty evidence rule", "evidence: an empty item")]
    [InlineData("a rule that measures nothing", "tagGrowth or clearFreed")]
    [InlineData("a five-letter tag", "tagGrowth tags")]
    [InlineData("a tag with a space in it", "tagGrowth tags")]
    [InlineData("a tag twice", "tagGrowth tags")]
    [InlineData("a tag with a line break at the end", "tagGrowth tags")]
    [InlineData("no tags", "tagGrowth tags: 1 to 16")]
    [InlineData("a rate of zero", "tagGrowth minMBPerHour")]
    [InlineData("a negative size", "clearFreed minMB")]
    [InlineData("a pool as a number", "pool")]
    [InlineData("an unknown pool", "pool")]
    [InlineData("a program with a path", "running image")]
    [InlineData("a program with a line break at the end", "running image")]
    [InlineData("a program with a look-alike letter", "running image")]
    [InlineData("a Store package with a line break at the end", "running storePackage")]
    [InlineData("a program by file and package", "running: give image or storePackage")]
    [InlineData("a program by name only", "running: give image or storePackage")]
    [InlineData("builds the wrong way round", "windowsBuild: build numbers")]
    [InlineData("an empty build range", "windowsBuild: give min, max or both")]
    [InlineData("a path as a file system", "fileSystems")]
    [InlineData("a file system with a line break at the end", "fileSystems")]
    [InlineData("no links", "links: 1 to 8")]
    [InlineData("an http link", "links url")]
    [InlineData("a link to another site", "links url")]
    [InlineData("a look-alike site", "links url")]
    [InlineData("a user name in a link", "links url")]
    [InlineData("a port in a link", "links url")]
    [InlineData("a site in capitals", "links url")]
    [InlineData("a quote in a link", "links url")]
    [InlineData("a space in a link", "links url")]
    [InlineData("a credit without what they did", "what")]
    [InlineData("a credit linking elsewhere", "credits url")]
    [InlineData("too many credits", "credits: 0 to 8")]
    public void AMalformedPackIsLeftOutWithAReason(string change, string reason)
    {
        var pack = Valid();
        var rule = pack["evidence"]![0]!.AsObject();
        var growth = rule["tagGrowth"]!.AsObject();
        switch (change)
        {
            case "format 2": pack["format"] = 2; break;
            case "format as text": pack["format"] = "1"; break;
            case "format 1.5": pack["format"] = 1.5; break;
            case "no format": pack.Remove("format"); break;
            case "no title": pack.Remove("title"); break;
            case "title null": pack["title"] = null; break;
            case "title a number": pack["title"] = 5; break;
            case "title too long": pack["title"] = new string('a', 61); break;
            case "title blank": pack["title"] = "   "; break;
            case "title with a space before it": pack["title"] = " A test issue"; break;
            case "a line break": pack["explanation"] = "What is\ngoing on."; break;
            case "a control character": pack["title"] = "A test\u0007 issue"; break;
            case "a right-to-left override": pack["workaround"] = "What \u202Espleh."; break;
            case "a zero-width space": pack["title"] = "A test\u200B issue"; break;
            case "a line separator": pack["title"] = "A test" + (char)0x2028 + "issue"; break;
            case "a paragraph separator": pack["title"] = "A test" + (char)0x2029 + "issue"; break;
            case "id in capitals": pack["id"] = "Test-Issue"; break;
            case "id with a path": pack["id"] = "../test"; break;
            // A $ in the checks would let each of these through: it also matches before a final line break.
            case "an id with a line break at the end": pack["id"] = "test-issue\n"; break;
            case "an unknown action": pack["actions"] = new JsonArray("defrag"); break;
            case "a command as the action": pack["actions"] = new JsonArray("cmd.exe /c del C:\\*"); break;
            case "an action twice": pack["actions"] = new JsonArray("auto-clear", "auto-clear"); break;
            case "an action without its suggestion": pack.Remove("suggestion"); break;
            case "a suggestion without an action": pack.Remove("actions"); break;
            case "no evidence": pack["evidence"] = new JsonArray(); break;
            case "an empty evidence rule": pack["evidence"] = new JsonArray((JsonNode?)null); break;
            case "a rule that measures nothing": rule.Remove("tagGrowth"); break;
            case "a five-letter tag": growth["tags"] = new JsonArray("Abcde"); break;
            case "a tag with a space in it": growth["tags"] = new JsonArray("A cd"); break;
            case "a tag twice": growth["tags"] = new JsonArray("Abcd", "Abcd"); break;
            case "a tag with a line break at the end": growth["tags"] = new JsonArray("Abcd\n"); break; // would make reading the tag throw
            case "no tags": growth["tags"] = new JsonArray(); break;
            case "a rate of zero": growth["minMBPerHour"] = 0; break;
            case "a negative size": rule["clearFreed"] = new JsonObject { ["tags"] = new JsonArray("Abcd"), ["name"] = "test records", ["minMB"] = -1 }; break;
            case "a pool as a number": growth["pool"] = 1; break;
            case "an unknown pool": growth["pool"] = "kernel"; break;
            case "a program with a path": rule["running"] = Programs(new JsonObject { ["name"] = "Test", ["image"] = @"C:\Tools\test.exe" }); break;
            case "a program with a line break at the end": rule["running"] = Programs(new JsonObject { ["name"] = "Test", ["image"] = "test.exe\n" }); break;
            case "a program with a look-alike letter": // the Kelvin sign, which a case-insensitive [A-Za-z] takes for a K
                rule["running"] = Programs(new JsonObject { ["name"] = "Test", ["image"] = (char)0x212A + "elvin.exe" }); break;
            case "a Store package with a line break at the end":
                rule["running"] = Programs(new JsonObject { ["name"] = "Test", ["storePackage"] = "Test.App\n" }); break;
            case "a program by file and package":
                rule["running"] = Programs(new JsonObject { ["name"] = "Test", ["image"] = "test.exe", ["storePackage"] = "Test.App" }); break;
            case "a program by name only": rule["running"] = Programs(new JsonObject { ["name"] = "Test" }); break;
            case "builds the wrong way round": rule["windowsBuild"] = new JsonObject { ["min"] = 27000, ["max"] = 26100 }; break;
            case "an empty build range": rule["windowsBuild"] = new JsonObject(); break;
            case "a path as a file system": rule["fileSystems"] = new JsonArray(@"C:\"); break;
            case "a file system with a line break at the end": rule["fileSystems"] = new JsonArray("ReFS\n"); break;
            case "no links": pack["links"] = new JsonArray(); break;
            case "an http link": Link(pack, "http://github.com/example/repo"); break;
            case "a link to another site": Link(pack, "https://example.com/report"); break;
            case "a look-alike site": Link(pack, "https://github.com.example.com/report"); break;
            case "a user name in a link": Link(pack, "https://github.com@example.com/report"); break;
            case "a port in a link": Link(pack, "https://github.com:8443/report"); break;
            case "a site in capitals": Link(pack, "https://GitHub.com/report"); break;
            case "a quote in a link": Link(pack, "https://github.com/a\"b"); break;
            case "a space in a link": Link(pack, "https://github.com/a b"); break;
            case "a credit without what they did": pack["credits"]![0]!.AsObject().Remove("what"); break;
            case "a credit linking elsewhere": pack["credits"]![0]!["url"] = "https://example.com/someone"; break;
            case "too many credits":
                pack["credits"] = new JsonArray(Enumerable.Range(0, 9).Select(i => (JsonNode?)new JsonObject { ["name"] = $"p{i}", ["what"] = "found it" }).ToArray()); break;
            default: throw new ArgumentException(change);
        }
        Assert.Contains(reason, Problem(pack.ToJsonString()));

        static JsonArray Programs(JsonObject program) => new(program);
        static void Link(JsonObject pack, string url) => pack["links"]![0]!["url"] = url;
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"a pack\"")]
    [InlineData("{\"format\": 1}")]
    [InlineData("{\"format\": 1, \"id\": \"a\", \"id\": \"b\"}")] // a repeated field could hide a reviewed value behind a second one
    public void TextThatIsNotAPackIsLeftOut(string json) => Assert.NotNull(Problem(json));

    [Fact]
    public void JsonExtrasAreNotAccepted()
    {
        string json = Valid().ToJsonString();
        Assert.NotNull(Problem(json.Replace("\"title\":\"A test issue\",", "\"title\":\"A test issue\", // a comment\n")));
        Assert.NotNull(Problem(json[..^1] + ",}")); // a trailing comma
        Assert.NotNull(Problem(json.Replace("\"title\":\"A test issue\"", "\"title\":\"A test issue\",\"title\":\"Another\"")));
    }

    [Fact]
    public void TextWithHalfASurrogatePairIsLeftOutNotThrown() =>
        // Only a caller of Parse can pass one: the built-in packs are read through a StreamReader, which replaces bad bytes.
        Assert.Contains("not a valid pack", Problem(Valid().ToJsonString().Replace("A test issue", "A test " + (char)0xD800 + "issue")));

    [Fact]
    public void ASizeAndADepthBoundHold()
    {
        var big = Valid();
        big["notes"] = new string('x', KnowledgeBase.MaxPackChars);
        Assert.Equal("larger than 32 KB", Problem(big.ToJsonString()));

        var deep = Valid();
        JsonNode nested = "end";
        for (int i = 0; i < 10; i++) nested = new JsonObject { ["more"] = nested };
        deep["notes"] = nested; // ignored as unknown, but still never read past eight levels
        Assert.Contains("not a valid pack", Problem(deep.ToJsonString()));
    }

    [Fact]
    public void FieldsANewerPackAddsAreIgnored()
    {
        var pack = Valid();
        pack["$schema"] = "./schema.json";
        pack["severity"] = "high";
        pack["evidence"]![0]!["handleGrowth"] = new JsonObject { ["type"] = "Token", ["perHour"] = 1000 };
        pack["evidence"]![0]!["tagGrowth"]!["window"] = "2h";
        pack["links"]![0]!["archived"] = true;
        var (issue, problem) = KnowledgeBase.Parse(pack.ToJsonString());
        Assert.Null(problem);
        Assert.Equal("A test issue", issue!.Title);
        Assert.Equal(10, issue.Evidence[0].TagGrowth!.MinMBPerHour);
    }

    // ------------------------------------------------------------------ loading many packs

    private static (string, string) Pack(string name, Action<JsonObject>? change = null)
    {
        var pack = Valid();
        pack["id"] = name;
        change?.Invoke(pack);
        return (name + ".json", pack.ToJsonString());
    }

    [Fact]
    public void ABadPackLeavesTheOthersAlone()
    {
        var knowledge = KnowledgeBase.Load([Pack("first"), ("broken.json", "{"), Pack("second", p => p["title"] = ""), Pack("third"), Pack("first")]);
        Assert.Equal(["first", "third"], knowledge.Entries.Select(e => e.Id));
        Assert.Equal(3, knowledge.Problems.Count);
        Assert.StartsWith("broken.json: ", knowledge.Problems[0]);
        Assert.Equal("second.json: title: 1 to 60 characters", knowledge.Problems[1]);
        Assert.Equal("first.json: the id first is already used", knowledge.Problems[2]);
    }

    [Fact]
    public void AtMostAHundredKnownIssuesAreKept()
    {
        var knowledge = KnowledgeBase.Load(Enumerable.Range(0, KnowledgeBase.MaxEntries + 2).Select(i => Pack($"issue-{i}")));
        Assert.Equal(KnowledgeBase.MaxEntries, knowledge.Entries.Count);
        Assert.Equal(["issue-100.json: more than 100 known issues", "issue-101.json: more than 100 known issues"], knowledge.Problems);
    }
}
