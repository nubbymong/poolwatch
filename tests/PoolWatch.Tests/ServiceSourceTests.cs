using System.Text.Json;
using PoolWatch.Core.Services;

namespace PoolWatch.Tests;

/// <summary>
/// The catalogue's optional "source" object: how it is read, what fails the load, and the lines the Services tab shows
/// for it. Each source line says what the source is written for, never that a service is safe to turn off.
/// </summary>
public class ServiceSourceTests
{
    /// <summary>A one-entry catalogue whose entry has <paramref name="source"/> as its "source" value (raw JSON), or none.</summary>
    private static ServiceCatalogue One(string? source) => ServiceCatalogue.Load(
        $$"""[{ "services": ["ExampleSvc"], "name": "Example Device Service", "category": "Telemetry and diagnostics", "lose": "nothing"{{(source is null ? "" : $", \"source\": {source}")}} }]""");

    private static ServiceSource SourceOf(string? source) => One(source).Find("ExampleSvc")!.Source;

    [Fact]
    public void AnEntryWithoutASourceHasNoOfficialSource()
    {
        var s = SourceOf(null);
        Assert.Same(ServiceSource.None, s);
        Assert.Equal(ServiceSourceKind.None, s.Kind);
        Assert.Equal("No official source", ServiceSourceText.Label(s.Kind));
    }

    [Fact]
    public void ANullSourceIsNoSource() => Assert.Equal(ServiceSource.None, SourceOf("null"));

    [Fact]
    public void AnEntryBuiltInCodeHasNoOfficialSource() => Assert.Equal(ServiceSource.None, new CatalogueEntry().Source);

    [Theory]
    [InlineData("wdot", ServiceSourceKind.Wdot)]
    [InlineData("cis", ServiceSourceKind.Cis)]
    [InlineData("both", ServiceSourceKind.Both)]
    [InlineData("none", ServiceSourceKind.None)]
    public void EachKindIsRead(string word, ServiceSourceKind kind)
    {
        var s = SourceOf($$"""{ "kind": "{{word}}" }""");
        Assert.Equal(kind, s.Kind);
        Assert.Null(s.WdotRef);
        Assert.Null(s.CisRef);
        Assert.Null(s.Checked);
    }

    [Fact]
    public void TheReferencesAndTheCheckedDateAreRead()
    {
        var s = SourceOf("""{ "kind": "both", "wdotRef": "Configurations/Example/Services.json: ExampleSvc", "cisRef": "5.1", "checked": "2026-10-30" }""");
        Assert.Equal(new ServiceSource(ServiceSourceKind.Both, "Configurations/Example/Services.json: ExampleSvc", "5.1", new DateOnly(2026, 10, 30)), s);
    }

    [Fact]
    public void PropertyNamesMatchInAnyCaseAndOthersAreSkipped()
    {
        var s = SourceOf("""{ "Kind": "cis", "CISREF": " 5.2 ", "note": "x", "nested": { "a": [1, { "kind": "bogus" }] }, "Checked": "2026-01-02" }""");
        Assert.Equal(new ServiceSource(ServiceSourceKind.Cis, null, "5.2", new DateOnly(2026, 1, 2)), s);
    }

    [Fact]
    public void CommentsAndAnEmptyReferenceAreAllowed()
    {
        var s = SourceOf("""{ /* checked by hand */ "kind": "wdot", "wdotRef": "  ", "cisRef": null, "checked": null, }""");
        Assert.Equal(new ServiceSource(ServiceSourceKind.Wdot), s);
    }

    [Fact]
    public void TheSourceStaysWithEveryServiceOfTheEntry()
    {
        var cat = ServiceCatalogue.Load("""[{ "services": ["ExampleA", "ExampleB"], "name": "Example family", "category": "x", "source": { "kind": "cis" } }]""");
        Assert.Equal(ServiceSourceKind.Cis, cat.Find("ExampleA")!.Source.Kind);
        Assert.Equal(ServiceSourceKind.Cis, cat.Find("exampleb")!.Source.Kind);
    }

    /// <summary>A kind that is not exactly one of the four words fails the load, so a typo is never read as another source.</summary>
    [Theory]
    [InlineData("\"microsoft\"")]
    [InlineData("\"WDOT\"")]
    [InlineData("\"Both\"")]
    [InlineData("\" wdot\"")]
    [InlineData("\"wdot, cis\"")]
    [InlineData("\"\"")]
    [InlineData("\"1\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[\"wdot\"]")]
    public void AnUnknownKindIsALoadError(string kind) =>
        Assert.Throws<JsonException>(() => One($$"""{ "kind": {{kind}} }"""));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "wdotRef": "Services.json" }""")]
    [InlineData("""{ "kinds": "wdot" }""")]
    public void ASourceWithoutAKindIsALoadError(string source) => Assert.Throws<JsonException>(() => One(source));

    [Theory]
    [InlineData("\"wdot\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("[{ \"kind\": \"wdot\" }]")]
    public void ASourceThatIsNotAnObjectIsALoadError(string source) => Assert.Throws<JsonException>(() => One(source));

    [Theory]
    [InlineData("\"2026-13-01\"")]
    [InlineData("\"2026-02-30\"")]
    [InlineData("\"30/10/2026\"")]
    [InlineData("\"2026-10-30T00:00:00\"")]
    [InlineData("\"2026-10-30 \"")]
    [InlineData("\"\"")]
    [InlineData("20261030")]
    [InlineData("{}")]
    public void ACheckedDateNotWrittenYearMonthDayIsALoadError(string date) =>
        Assert.Throws<JsonException>(() => One($$"""{ "kind": "wdot", "checked": {{date}} }"""));

    [Theory]
    [InlineData("wdotRef", "5")]
    [InlineData("cisRef", "true")]
    [InlineData("wdotRef", "{}")]
    [InlineData("cisRef", "[\"5.1\"]")]
    public void AReferenceThatIsNotTextIsALoadError(string property, string value) =>
        Assert.Throws<JsonException>(() => One($$"""{ "kind": "both", "{{property}}": {{value}} }"""));

    /// <summary>A load error says what was read where a value was refused, so a typo in the data can be found.</summary>
    [Theory]
    [InlineData("""{ "kind": "wodt" }""", "\"kind\"", "\"wodt\"")]
    [InlineData("""{ "kind": 1 }""", "\"kind\"", "a number")]
    [InlineData("""{ "kind": null }""", "\"kind\"", "null")]
    [InlineData("""{ "kind": "wdot", "checked": "30/10/2026" }""", "\"checked\"", "\"30/10/2026\"")]
    [InlineData("""{ "kind": "both", "cisRef": true }""", "\"cisRef\"", "true")]
    [InlineData("""{ "kind": "both", "wdotRef": ["x"] }""", "\"wdotRef\"", "a list")]
    [InlineData("""{ "kind": false }""", "\"kind\"", "false")]
    [InlineData("""{ "kind": "both", "cisRef": {} }""", "\"cisRef\"", "an object")]
    [InlineData("\"wdot\"", "\"source\"", "\"wdot\"")]
    public void ALoadErrorSaysWhatWasRead(string source, string property, string shown)
    {
        var ex = Assert.Throws<JsonException>(() => One(source));
        Assert.Contains(property, ex.Message);
        Assert.EndsWith($", not {shown}.", ex.Message);
    }

    [Fact]
    public void ASourceWritesBackAsItReads()
    {
        var s = new ServiceSource(ServiceSourceKind.Both, "Services.json: ExampleSvc", "5.3", new DateOnly(2026, 10, 30));
        string json = JsonSerializer.Serialize(s);
        Assert.Equal("""{"kind":"both","wdotRef":"Services.json: ExampleSvc","cisRef":"5.3","checked":"2026-10-30"}""", json);
        Assert.Equal(s, JsonSerializer.Deserialize<ServiceSource>(json));
        Assert.Equal("""{"kind":"none"}""", JsonSerializer.Serialize(ServiceSource.None));
    }

    /// <summary>Writing a kind that is not one of the four fails as reading one does: nothing is written that would not read back.</summary>
    [Fact]
    public void WritingAKindThatIsNotOneOfTheFourIsAnError()
    {
        var ex = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(new ServiceSource((ServiceSourceKind)99)));
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void TheLinesSayWhatEachSourceIsFor()
    {
        Assert.Equal("Microsoft's virtual-desktop optimisation tool turns this off", ServiceSourceText.Label(ServiceSourceKind.Wdot));
        Assert.Equal("The CIS security checklist for company PCs turns this off", ServiceSourceText.Label(ServiceSourceKind.Cis));
        Assert.Equal("Microsoft's virtual-desktop tool and the CIS security checklist turn this off", ServiceSourceText.Label(ServiceSourceKind.Both));
        Assert.Equal("No official source", ServiceSourceText.Label(ServiceSourceKind.None));
        Assert.Equal("These sources were written for virtual desktops and company PCs. Check what you lose first.", ServiceSourceText.GroupCaveat);
    }

    [Fact]
    public void AValueThatIsNotOneOfTheFourClaimsNoSource() =>
        Assert.Equal(ServiceSourceText.NoneLabel, ServiceSourceText.Label((ServiceSourceKind)99));

    [Fact]
    public void NoLineSaysSafeOrJustMicrosoftsGuidance()
    {
        string[] lines = [.. Enum.GetValues<ServiceSourceKind>().Select(ServiceSourceText.Label), ServiceSourceText.GroupCaveat];
        Assert.Equal(5, lines.Distinct().Count());
        foreach (var line in lines)
        {
            Assert.DoesNotContain("safe", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guidance", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("!", line);
        }
    }

    /// <summary>
    /// Loads each row of a catalogue file on its own and gives one line per row that fails to load: its number, its
    /// "name" and the load error. A typo in one of many rows then says where it is: System.Text.Json keeps the path out
    /// of an error that has its own message, and the whole file's load stops at the first bad row.
    /// </summary>
    private static List<string> RowsThatDoNotLoad(string catalogueJson)
    {
        var failures = new List<string>();
        using var doc = JsonDocument.Parse(catalogueJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        int row = 0;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            row++;
            try { ServiceCatalogue.Load($"[{e.GetRawText()}]"); }
            catch (JsonException ex)
            {
                string name = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : "no name";
                failures.Add($"row {row} ({name}): {ex.Message}");
            }
        }
        return failures;
    }

    private static string ShippedCatalogueJson()
    {
        using var s = typeof(ServiceCatalogue).Assembly.GetManifestResourceStream("services-catalogue.json");
        Assert.NotNull(s);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [Fact]
    public void ABadSourceInOneOfManyRowsIsNamedWithWhatWasRead()
    {
        var failures = RowsThatDoNotLoad("""
            [
              { "services": ["ExampleA"], "name": "Example A", "category": "x", "source": { "kind": "wdot" } },
              { "services": ["ExampleB"], "name": "Example B", "category": "x", "source": { "kind": "wodt" } },
              { "services": ["ExampleC"], "name": "Example C", "category": "x" },
            ]
            """);
        string failure = Assert.Single(failures);
        Assert.StartsWith("row 2 (Example B): ", failure);
        Assert.Contains("\"wodt\"", failure);
    }

    /// <summary>
    /// The shipped catalogue loads under the source contract, one row at a time, so a row that fails names itself and what
    /// was refused. Loading is the whole check: a row that loads has a source of one of the four kinds (none when it has
    /// no "source"). Whether a row should carry a source, and its references and date, is for the catalogue data's own
    /// tests. The whole file then loads the way the app loads it.
    /// </summary>
    [Fact]
    public void EveryShippedRowHasAReadableSource()
    {
        var failures = RowsThatDoNotLoad(ShippedCatalogueJson());
        if (failures.Count > 0) Assert.Fail(string.Join(Environment.NewLine, failures));
        Assert.NotEmpty(DefaultCatalogue.Load().Entries);
    }
}
