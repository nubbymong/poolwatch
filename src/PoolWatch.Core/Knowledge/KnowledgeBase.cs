using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PoolWatch.Core.Knowledge;

/// <summary>
/// The known issues PoolWatch knows: the knowledge packs built into it (knowledge/*.json, one known issue per file), read
/// and checked once. PoolWatch runs as administrator, so a pack is read as untrusted even though each one is reviewed in
/// the repository: a file is at most <see cref="MaxPackChars"/> characters, its JSON is read with strict types and no
/// repeated fields, and every value is checked against the limits in docs/knowledge-packs.md and knowledge/schema.json.
/// Fields this version does not know are ignored, so a pack written for a newer PoolWatch still loads. A pack that fails
/// a check is left out whole, with the reason in <see cref="Problems"/>; the others are not affected.
/// </summary>
public sealed partial class KnowledgeBase
{
    /// <summary>The pack format this version reads ("format": 1); a pack of another format is left out.</summary>
    public const int Format = 1;
    public const int MaxPackChars = 32 * 1024, MaxEntries = 100;
    public const int MaxId = 64, MaxTitle = 60, MaxExplanation = 600, MaxWorkaround = 400, MaxSuggestion = 160, MaxName = 40,
        MaxLinkTitle = 100, MaxCreditWhat = 200, MaxUrl = 300;
    public const int MaxRules = 8, MaxTags = 16, MaxPrograms = 16, MaxFileSystems = 8, MaxActions = 3, MaxLinks = 8, MaxCredits = 8;
    /// <summary>The most a rule may ask for, in MB (a petabyte; anything more is a typing error).</summary>
    public const double MaxMB = 1 << 30;

    /// <summary>The sites a pack's links may point at: the public reports and Microsoft's documentation.</summary>
    public static readonly IReadOnlySet<string> LinkHosts = new HashSet<string>(StringComparer.Ordinal) { "github.com", "learn.microsoft.com", "support.microsoft.com" };

    /// <summary>The embedded packs' resource names start with this (PoolWatch.Core.csproj).</summary>
    private const string ResourcePrefix = "knowledge/";

    private KnowledgeBase(IReadOnlyList<KnownIssue> entries, IReadOnlyList<string> problems)
    {
        Entries = entries;
        Problems = problems;
        GrowthTags = entries.SelectMany(e => e.Evidence).SelectMany(r => r.TagGrowth?.Tags ?? []).Select(PoolTag.Parse).ToHashSet();
    }

    public IReadOnlyList<KnownIssue> Entries { get; }
    /// <summary>Why a pack was left out, "name: reason", for the log.</summary>
    public IReadOnlyList<string> Problems { get; }
    /// <summary>The pool tags a growth rule names: the only tags whose growth the evidence needs.</summary>
    public IReadOnlySet<PoolTag> GrowthTags { get; }

    /// <summary>The packs built into PoolWatch, read on first use.</summary>
    public static KnowledgeBase Embedded() => EmbeddedPacks.Value;

    private static readonly Lazy<KnowledgeBase> EmbeddedPacks = new(() =>
    {
        var assembly = typeof(KnowledgeBase).Assembly;
        return Load(assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(n => (n[ResourcePrefix.Length..], ReadBounded(assembly.GetManifestResourceStream(n)!))));
    });

    /// <summary>The packs, in order; one that fails a check, repeats an id or comes after <see cref="MaxEntries"/> is left out.</summary>
    public static KnowledgeBase Load(IEnumerable<(string Name, string Json)> packs)
    {
        var entries = new List<KnownIssue>();
        var problems = new List<string>();
        foreach (var (name, json) in packs)
        {
            var (issue, problem) = Parse(json);
            if (issue is not null && entries.Any(e => e.Id == issue.Id)) problem = $"the id {issue.Id} is already used";
            else if (issue is not null && entries.Count == MaxEntries) problem = $"more than {MaxEntries} known issues";
            if (problem is null) entries.Add(issue!);
            else problems.Add($"{name}: {problem}");
        }
        return new KnowledgeBase(entries, problems);
    }

    /// <summary>One pack: the known issue it holds, or why it is not used. Never throws.</summary>
    public static (KnownIssue? Issue, string? Problem) Parse(string json)
    {
        if (json.Length > MaxPackChars) return (null, $"larger than {MaxPackChars / 1024} KB");
        try
        {
            using var doc = JsonDocument.Parse(json, DocumentOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, "not a JSON object");
            if (!doc.RootElement.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out int f) || f != Format)
                return (null, $"\"format\" must be {Format}");
            var issue = doc.RootElement.Deserialize<KnownIssue>(SerializerOptions)!;
            return Invalid(issue) is { } problem ? (null, problem) : (issue, null);
        }
        // ArgumentException: text with half of a surrogate pair cannot be read as JSON at all.
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) { return (null, "not a valid pack: " + ex.Message); }
    }

    /// <summary>Whether a web address is one of the packs' links or credits (a page PoolWatch may open from a note).</summary>
    public bool HasLink(string url) => Entries.Any(e => e.Links.Any(l => l.Url == url) || e.Credits.Any(c => c.Url == url));

    /// <summary>A resource's text, at most one character more than a pack may hold (so a larger one is seen and left out).</summary>
    private static string ReadBounded(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxPackChars + 1];
        int total = 0, n;
        while (total < buffer.Length && (n = reader.Read(buffer, total, buffer.Length - total)) > 0) total += n;
        return new string(buffer, 0, total);
    }

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 8, AllowDuplicateProperties = false };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 8, AllowDuplicateProperties = false, RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    // ------------------------------------------------------------------ the checks (each says which field and why)

    private static string? Invalid(KnownIssue e) =>
        (e.Id.Length <= MaxId && IdText().IsMatch(e.Id) ? null : $"id: lower-case letters and digits, words joined by hyphens, at most {MaxId} characters")
        ?? Text("title", e.Title, MaxTitle) ?? Text("explanation", e.Explanation, MaxExplanation) ?? Text("workaround", e.Workaround, MaxWorkaround)
        ?? Actions(e)
        ?? Count("evidence", e.Evidence.Count, 1, MaxRules) ?? First("evidence", e.Evidence, Rule)
        ?? Count("links", e.Links.Count, 1, MaxLinks) ?? First("links", e.Links, l => Text("links title", l.Title, MaxLinkTitle) ?? Url("links url", l.Url))
        ?? Count("credits", e.Credits.Count, 0, MaxCredits)
        ?? First("credits", e.Credits, c => Text("credits name", c.Name, MaxName) ?? Text("credits what", c.What, MaxCreditWhat) ?? (c.Url is null ? null : Url("credits url", c.Url)));

    private static string? Actions(KnownIssue e)
    {
        if (e.Actions.Count > MaxActions || e.Actions.Distinct().Count() != e.Actions.Count) return $"actions: at most {MaxActions}, each once";
        if (e.Actions.Any(a => KnownActions.Find(a) is null)) return "actions: only the built-in action ids in docs/knowledge-packs.md";
        if (e.Actions.Count == 0) return e.Suggestion is null ? null : "suggestion: shown only on an action's suggestion, and there is no action";
        return Text("suggestion", e.Suggestion, MaxSuggestion);
    }

    private static string? Rule(EvidenceRule r) =>
        (r.TagGrowth is null && r.ClearFreed is null ? "evidence: each rule needs tagGrowth or clearFreed, so an issue is named only on what PoolWatch measured" : null)
        ?? (r.TagGrowth is { } g ? Tags("tagGrowth", g.Tags) ?? Text("tagGrowth name", g.Name, MaxName) ?? Amount("tagGrowth minMBPerHour", g.MinMBPerHour) : null)
        ?? (r.ClearFreed is { } c ? Tags("clearFreed", c.Tags) ?? Text("clearFreed name", c.Name, MaxName) ?? Amount("clearFreed minMB", c.MinMB) : null)
        ?? (r.Running is { } running ? Count("running", running.Count, 1, MaxPrograms) ?? First("running", running, Program) : null)
        ?? (r.WindowsBuild is { } b ? Build(b) : null)
        ?? (r.FileSystems is { } fs
            ? Count("fileSystems", fs.Count, 1, MaxFileSystems) ?? (fs.All(s => s is not null && FileSystemText().IsMatch(s)) ? null : "fileSystems: names such as ReFS or NTFS")
            : null);

    private static string? Program(ProcessRule p) =>
        Text("running name", p.Name, MaxName) ?? (p.Image, p.StorePackage) switch
        {
            ({ } image, null) => ImageText().IsMatch(image) ? null : "running image: a program's file name, such as ChatGPT.exe",
            (null, { } package) => PackageText().IsMatch(package) ? null : "running storePackage: the start of a Store package's name, such as OpenAI.Codex",
            _ => "running: give image or storePackage, not both",
        };

    private static string? Build(BuildRange b) =>
        b.Min is null && b.Max is null ? "windowsBuild: give min, max or both"
        : b.Min is < 1 or > 99999 || b.Max is < 1 or > 99999 || b.Min > b.Max ? "windowsBuild: build numbers from 1 to 99999, min no higher than max"
        : null;

    /// <summary>1 to <see cref="MaxTags"/> different pool tags of 1 to 4 printable characters (a shorter tag is padded with spaces, as Windows does).</summary>
    private static string? Tags(string field, IReadOnlyList<string> tags) =>
        Count(field + " tags", tags.Count, 1, MaxTags)
        ?? (tags.All(t => t is not null && TagText().IsMatch(t)) && tags.Distinct().Count() == tags.Count ? null : $"{field} tags: each 1 to 4 printable characters, each once");

    private static string? Amount(string field, double mb) => double.IsFinite(mb) && mb > 0 && mb <= MaxMB ? null : $"{field}: more than 0, at most {MaxMB:0}";

    /// <summary>
    /// Plain text on one line: 1 to <paramref name="max"/> characters, no spaces around it, and no control or invisible
    /// formatting characters (which could hide or reorder words from the person who reviews the pack).
    /// </summary>
    private static string? Text(string field, string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > max) return $"{field}: 1 to {max} characters";
        if (s != s.Trim()) return $"{field}: no spaces at the start or end";
        return s.Any(c => char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                              or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
            ? $"{field}: no control or invisible characters" : null;
    }

    /// <summary>
    /// An https:// address on one of <see cref="LinkHosts"/>, written plainly (lower-case site, no user name, no port) with
    /// letters, digits and - . _ ~ : / ? # = &amp; % + only, so it can never carry a space or a quote onto a command line.
    /// </summary>
    private static string? Url(string field, string url) =>
        url.Length <= MaxUrl && url.All(c => char.IsAsciiLetterOrDigit(c) || "-._~:/?#=&%+".Contains(c))
        && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && LinkHosts.Contains(u.Host)
        && url.StartsWith($"https://{u.Host}/", StringComparison.Ordinal)
            ? null : $"{field}: an https:// address on {string.Join(", ", LinkHosts)}, at most {MaxUrl} characters";

    private static string? Count(string field, int count, int min, int max) => count >= min && count <= max ? null : $"{field}: {min} to {max} items";

    /// <summary>The first item's problem. A list item may be JSON null (the reader checks nullability on fields, not inside lists).</summary>
    private static string? First<T>(string field, IEnumerable<T?> items, Func<T, string?> check) where T : class =>
        items.Select(i => i is null ? $"{field}: an empty item" : check(i)).FirstOrDefault(p => p is not null);

    // \z, not $: $ also matches before a final line break. No IgnoreCase: under it, [A-Za-z] also matches the Kelvin sign.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")] private static partial Regex IdText();
    [GeneratedRegex(@"^[\x21-\x7E]{1,4}\z")] private static partial Regex TagText();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,59}\.[eE][xX][eE]\z")] private static partial Regex ImageText();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.-]{0,49}\z")] private static partial Regex PackageText();
    [GeneratedRegex(@"^[A-Za-z0-9]{1,16}\z")] private static partial Regex FileSystemText();
}
