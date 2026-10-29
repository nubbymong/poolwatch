using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PoolWatch.Core.Services.Agent;

/// <summary>
/// An item the answer names, as the review shows it: what it is, the name the answer used, its name on this PC and the
/// agent's reason. For an item the answer's name matched alone (planned, off already, kept, or refused by the gate),
/// <see cref="Name"/> never reads the same as text as another such item's of the same kind, in any case: it says where a
/// startup entry lives or a task's path, or at the last it is the item's id in the prompt, when its name alone would read
/// the same. A name that matched nothing, or more than one item, is shown as the answer gave it, cut, and two of those can
/// read alike, so the review never keys its rows by <see cref="Name"/>. Reading differently is not proof of a place: a
/// startup entry's name is chosen by whoever made it and can hold text that looks like a place ("Foo (HKLM Run)"), or
/// letters that only look like another's. The review shows a planned entry's place and a task's path in a line of their
/// own, never inside the name.
/// </summary>
public sealed record AgentPlanItem(AgentItemKind Kind, string Id, string Name, string? Reason);

/// <summary>A service the plan turns off, through Apply after the user confirms: its option as it is now and the start type Apply sets.</summary>
public sealed record PlannedService(AgentPlanItem Item, ServiceOption Option, ServiceStartType NewStart);

/// <summary>A startup entry the plan stops starting at sign-in (the StartupApproved switch, as Task Manager's).</summary>
public sealed record PlannedStartup(AgentPlanItem Item, AgentStartupEntry Entry);

/// <summary>A scheduled task the plan disables (its Enabled flag).</summary>
public sealed record PlannedTask(AgentPlanItem Item, AgentScheduledTask Task);

/// <summary>Something the answer asked to turn off that PoolWatch will not, and why.</summary>
public sealed record RefusedItem(AgentPlanItem Item, string Why);

/// <summary>
/// What an imported answer would do, for the user to review: nothing in it has been changed. The services go through the
/// Services tab's Apply (its own question, its gate again against the live services, the original recorded first), the
/// startup entries and tasks through the Tasks and startup apps tab's switch, each with <see cref="AgentAnswer.Via"/>.
/// </summary>
public sealed record AgentPlan
{
    public IReadOnlyList<PlannedService> Services { get; init; } = [];
    public IReadOnlyList<PlannedStartup> Startup { get; init; } = [];
    public IReadOnlyList<PlannedTask> Tasks { get; init; } = [];
    /// <summary>Asked to be turned off, and off already: nothing to do.</summary>
    public IReadOnlyList<AgentPlanItem> AlreadyOff { get; init; } = [];
    /// <summary>The agent says to keep these: nothing changes.</summary>
    public IReadOnlyList<AgentPlanItem> Kept { get; init; } = [];
    /// <summary>Asked to be turned off, and refused, each with why.</summary>
    public IReadOnlyList<RefusedItem> Refused { get; init; } = [];
    /// <summary>Names to keep that are not on PoolWatch's list now (nothing to do).</summary>
    public IReadOnlyList<string> KeepNotFound { get; init; } = [];

    /// <summary>Anything to change.</summary>
    public bool HasChanges => Services.Count + Startup.Count + Tasks.Count > 0;

    /// <summary>The services to tick on the Services tab and pass to its Apply, by their names on this PC.</summary>
    public IReadOnlyList<string> ServiceNames => Services.Select(s => s.Option.Service.Name).ToList();

    /// <summary>
    /// The plan's services as a service profile (the form settings.json keeps), for "Save as profile". A profile holds only
    /// what the catalogue lists: <see cref="ServiceProfileRules.Clean"/> leaves out the rest when it is read, and says so.
    /// </summary>
    public ServiceProfile AsProfile(string name) => new(name, ServiceNames);
}

/// <summary>The result of reading an answer: the plan, or why the whole answer was refused.</summary>
public sealed record AgentAnswerResult(AgentPlan? Plan, string? Error)
{
    public static AgentAnswerResult Refused(string why) => new(null, why);
}

/// <summary>
/// Reads the AI agent's answer (poolwatch-services-answer.json) strictly, and turns it into a plan against the items on this
/// PC now. The answer is untrusted: it is bounded (<see cref="MaxBytes"/>), must be exactly the documented form (version 1;
/// "turnOff" and "keep" with "services", "startup" and "tasks" lists of names; "why" with one text per name; nothing else,
/// no field twice, a name in "why" not twice in any case; valid Unicode), and a name only ever picks an item from
/// <see cref="AgentItemList"/>. Anything else refuses the whole answer, with why; reading never throws. Nothing in it is run, opened or
/// written anywhere: a reason is kept as plain text, cleaned and bounded, for the review. Each service goes through Apply's
/// gate (<see cref="ServicePlan.BuildApply"/>) as a profile's do, so a protected service, an Insider service while the PC
/// is enrolled or a driver is refused with why; unlike a profile applied at start, a third-party service the catalogue does
/// not know may be planned, as Apply allows it (deciding those is what the agent is for).
/// </summary>
public static class AgentAnswer
{
    public const string FileName = AgentPrompt.AnswerFileName;
    public const int Version = 1;
    /// <summary>The largest answer read: far more than any answer needs.</summary>
    public const int MaxBytes = 64 * 1024;
    /// <summary>The most names in one list, and the longest name and reason read, in characters.</summary>
    public const int MaxNamesPerList = 500, MaxNameChars = 512, MaxReasonChars = 300;
    /// <summary>The Via of every change the plan leads to (Active changes shows where a change came from).</summary>
    public const string Via = "AI agent answer";

    private static readonly string[] Lists = ["services", "startup", "tasks"];

    /// <summary>Reads an answer file's bytes (a UTF-8 byte order mark is allowed). The caller reads at most <see cref="MaxBytes"/> + 1 bytes.</summary>
    public static AgentAnswerResult Read(ReadOnlySpan<byte> utf8, AgentItemList current)
    {
        if (utf8.Length > MaxBytes) return AgentAnswerResult.Refused(TooLarge);
        ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
        if (utf8.StartsWith(bom)) utf8 = utf8[bom.Length..];
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8); }
        catch (DecoderFallbackException) { return AgentAnswerResult.Refused("The answer is not text PoolWatch can read (it is not UTF-8)."); }
        return Read(text, current);
    }

    /// <summary>
    /// Reads an answer's text against the items on this PC now (<paramref name="current"/>, built again just before). Never
    /// throws for what the file holds: anything it cannot read is refused with a plain reason.
    /// </summary>
    public static AgentAnswerResult Read(string text, AgentItemList current)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return AgentAnswerResult.Refused(TooLarge);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        Answer answer;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            answer = Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return AgentAnswerResult.Refused(ex is NotOurs ? ex.Message
                : $"The answer is not valid JSON{(ex.LineNumber is long line ? $" (line {(line + 1).ToString(CultureInfo.InvariantCulture)})" : "")}.");
        }
        // JSON may write half of a surrogate pair as a \u escape: the parse takes it, and reading that name or text throws
        // (InvalidOperationException). Text given as a string may hold one as it is: the parse throws (ArgumentException).
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return AgentAnswerResult.Refused(NotUnicode);
        }
        return new(Plan(answer, current), null);
    }

    private static string TooLarge => $"The answer is larger than {MaxBytes / 1024} KB, far more than an answer needs, so PoolWatch did not read it.";

    private const string NotUnicode = "The answer is not valid JSON (it holds text that is not valid Unicode).";

    /// <summary>The answer is JSON, but not in the documented form: its message says how, for the user.</summary>
    private sealed class NotOurs(string message) : JsonException(message);

    private sealed record Answer(Dictionary<string, List<string>> TurnOff, Dictionary<string, List<string>> Keep, Dictionary<string, string> Why);

    private static Answer Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new NotOurs("The answer is not in PoolWatch's answer format: it must be one JSON object.");
        int? version = null;
        Dictionary<string, List<string>>? turnOff = null, keep = null;
        Dictionary<string, string>? why = null;
        foreach (var p in Properties(root, "the answer"))
        {
            switch (p.Name)
            {
                case "version":
                    version = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int v) ? v : -1;
                    break;
                case "turnOff": turnOff = NameLists(p.Value, "turnOff"); break;
                case "keep": keep = NameLists(p.Value, "keep"); break;
                case "why": why = Reasons(p.Value); break;
                default: throw new NotOurs($"The answer has a field PoolWatch does not know: \"{Shown(p.Name)}\". It reads only version, turnOff, keep and why.");
            }
        }
        if (version != Version) throw new NotOurs($"The answer is not in a format this PoolWatch reads: \"version\" must be {Version}.");
        return new(turnOff ?? [], keep ?? [], why ?? new(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An object's properties, refusing one that appears twice (JSON allows it; the answer may not). "Twice" is as the
    /// object's names are matched: exactly for the format's own fields, in any case for "why", whose names are items'.
    /// </summary>
    private static List<JsonProperty> Properties(JsonElement e, string what, StringComparer? names = null)
    {
        var seen = new HashSet<string>(names ?? StringComparer.Ordinal);
        var list = new List<JsonProperty>();
        foreach (var p in e.EnumerateObject())
        {
            if (!seen.Add(p.Name)) throw new NotOurs($"The answer has \"{Shown(p.Name)}\" twice in {what}.");
            list.Add(p);
        }
        return list;
    }

    /// <summary>{"services": [...], "startup": [...], "tasks": [...]}, each optional, each a list of names.</summary>
    private static Dictionary<string, List<string>> NameLists(JsonElement e, string field)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new NotOurs($"\"{field}\" in the answer must be an object with services, startup and tasks lists.");
        var lists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in Properties(e, $"\"{field}\""))
        {
            if (!Lists.Contains(p.Name)) throw new NotOurs($"\"{field}\" in the answer has a list PoolWatch does not know: \"{Shown(p.Name)}\". It reads only services, startup and tasks.");
            if (p.Value.ValueKind != JsonValueKind.Array) throw new NotOurs($"\"{field}\".\"{p.Name}\" in the answer must be a list of names.");
            var names = new List<string>();
            foreach (var x in p.Value.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String || x.GetString() is not { Length: > 0 and <= MaxNameChars } name)
                    throw new NotOurs($"\"{field}\".\"{p.Name}\" in the answer must hold only names, each 1 to {MaxNameChars} characters.");
                names.Add(name);
            }
            if (names.Count > MaxNamesPerList) throw new NotOurs($"\"{field}\".\"{p.Name}\" in the answer lists more than {MaxNamesPerList} names.");
            lists[p.Name] = names;
        }
        return lists;
    }

    /// <summary>{"name": "reason", ...}: a reason per name, kept as plain text, cleaned and bounded.</summary>
    private static Dictionary<string, string> Reasons(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new NotOurs("\"why\" in the answer must be an object with one reason per name.");
        var why = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var props = Properties(e, "\"why\"", StringComparer.OrdinalIgnoreCase);
        if (props.Count > 3 * MaxNamesPerList) throw new NotOurs($"\"why\" in the answer holds more than {3 * MaxNamesPerList} reasons.");
        foreach (var p in props)
        {
            if (p.Value.ValueKind != JsonValueKind.String) throw new NotOurs($"Each reason in \"why\" must be text; \"{Shown(p.Name)}\"'s is not.");
            if (p.Name.Length is 0 or > MaxNameChars) throw new NotOurs($"\"why\" in the answer names something that is not a name of 1 to {MaxNameChars} characters.");
            why[p.Name] = AgentItemList.Cut(AgentItemList.Plain(p.Value.GetString()!), MaxReasonChars);
        }
        return why;
    }

    /// <summary>A name from the answer as an error or the review may show it: one plain line of at most 80 characters.</summary>
    internal static string Shown(string name) => AgentItemList.Cut(AgentItemList.Plain(name), 80);

    private static AgentItemKind KindOf(string list) => list switch
    {
        "services" => AgentItemKind.Service,
        "startup" => AgentItemKind.Startup,
        _ => AgentItemKind.Task,
    };

    private static AgentPlan Plan(Answer a, AgentItemList current)
    {
        var services = new List<PlannedService>();
        var startup = new List<PlannedStartup>();
        var tasks = new List<PlannedTask>();
        var already = new List<AgentPlanItem>();
        var kept = new List<AgentPlanItem>();
        var refused = new List<RefusedItem>();
        var keepNotFound = new List<string>();

        foreach (var list in Lists)
        {
            var kind = KindOf(list);
            var names = ReviewNames(current, kind);
            var keepIds = (a.Keep.GetValueOrDefault(list) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A name given twice (in any case) is taken once. Each item has one id and names match it in any case, so no two
            // names left after this pick the same item: nothing is planned twice.
            foreach (var id in (a.TurnOff.GetValueOrDefault(list) ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? reason = a.Why.GetValueOrDefault(id);
                var found = current.Find(kind, id);
                var item = new AgentPlanItem(kind, Shown(id), found.Count == 1 ? names[found[0]] : Shown(id), reason);
                if (keepIds.Contains(id)) { refused.Add(new(item, "The answer both turns it off and keeps it, so it is left as it is.")); continue; }
                if (found.Count == 0) { refused.Add(new(item, NotFound(kind, id, current))); continue; }
                if (found.Count > 1) { refused.Add(new(item, "That name matches more than one item on this PC, so PoolWatch cannot tell which one is meant.")); continue; }
                var match = found[0];
                switch (kind)
                {
                    case AgentItemKind.Service:
                        var o = match.Service!;
                        if (WhyNotService(o, current) is { } no) refused.Add(new(item, no));
                        else if (ServicePlan.OffAlready(o)) already.Add(item);
                        else services.Add(new(item, o, ServicePlan.OffStart(o)));
                        break;
                    case AgentItemKind.Startup:
                        if (match.Startup!.Enabled) startup.Add(new(item, match.Startup)); else already.Add(item);
                        break;
                    default:
                        if (match.Task!.Enabled) tasks.Add(new(item, match.Task)); else already.Add(item);
                        break;
                }
            }
            var turnOffIds = (a.TurnOff.GetValueOrDefault(list) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in (a.Keep.GetValueOrDefault(list) ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (turnOffIds.Contains(id)) continue; // refused above
                var found = current.Find(kind, id);
                if (found.Count == 0) keepNotFound.Add(Shown(id));
                else kept.Add(new AgentPlanItem(kind, Shown(id), found.Count == 1 ? names[found[0]] : Shown(id), a.Why.GetValueOrDefault(id)));
            }
        }
        return new AgentPlan
        {
            Services = services, Startup = startup, Tasks = tasks, AlreadyOff = already, Kept = kept, Refused = refused, KeepNotFound = keepNotFound,
        };
    }

    /// <summary>
    /// The name the review shows each item of <paramref name="kind"/> on this PC by. An item is shown by its name (a service by
    /// its display name and name), with where a startup entry lives or a task's path when its id in the prompt carries that
    /// (<see cref="AgentItem.ToldApart"/>). The review cuts names shorter than ids are cut (<see cref="Shown"/>), so two can
    /// still read the same here, in any case: each item that does is shown with its place or path, and then, if it still
    /// reads like another, by its id in the prompt. That is checked again until nothing changes; an item only ever moves on,
    /// at most twice, so this ends. Items left alike all share one id, and an answer naming it is refused, so an item the plan
    /// shows by its name here (one the answer's name picked alone) never reads the same as text as another of its kind. Text
    /// that differs is all this promises: a name can hold what looks like a place, or letters that only look like another's.
    /// </summary>
    private static Dictionary<AgentItem, string> ReviewNames(AgentItemList current, AgentItemKind kind)
    {
        var items = current.Items.Where(i => i.Kind == kind).ToList();
        var step = items.Select(i => i.ToldApart ? WithPlace : ByName).ToArray();
        var names = items.Select((i, n) => ReviewName(i, step[n])).ToArray();
        for (bool changed = true; changed;)
        {
            changed = false;
            var counts = AgentItemList.Counts(names);
            for (int n = 0; n < items.Count; n++)
            {
                if (step[n] == ById || counts[names[n]] < 2) continue;
                step[n] = kind == AgentItemKind.Service ? ById : step[n] + 1;
                names[n] = ReviewName(items[n], step[n]);
                changed = true;
            }
        }
        var byItem = new Dictionary<AgentItem, string>(ReferenceEqualityComparer.Instance);
        for (int n = 0; n < items.Count; n++) byItem[items[n]] = names[n];
        return byItem;
    }

    /// <summary>The steps of an item's name in the review (<see cref="ReviewNames"/>).</summary>
    private const int ByName = 0, WithPlace = 1, ById = 2;

    private static string ReviewName(AgentItem i, int step) => step == ById ? i.Id : i.Kind switch
    {
        AgentItemKind.Service => i.Service!.Service.DisplayName is { Length: > 0 } d && d != i.Service.Service.Name
            ? $"{Shown(d)} ({Shown(i.Service.Service.Name)})" : Shown(i.Service!.Service.Name),
        AgentItemKind.Startup => Placed(i.Startup!.Name, step == WithPlace ? i.Startup.Location : ""),
        _ => Placed(i.Task!.Name, step == WithPlace ? i.Task.Path : ""),
    };

    /// <summary>A name, with where it lives after it when there is a place to say.</summary>
    private static string Placed(string name, string place) => Shown(place) is { Length: > 0 } p ? $"{Shown(name)} ({p})" : Shown(name);

    /// <summary>
    /// Why a service the answer names is not turned off, or null: Apply's gate (<see cref="ServicePlan.BuildApply"/>, which a
    /// profile's services go through too) with the protected reason put plainly.
    /// </summary>
    private static string? WhyNotService(ServiceOption o, AgentItemList current)
    {
        var s = o.Service;
        if (ServiceCatalogue.ProtectedReason(s.Name, current.InsiderEnrolled) is { } protectedWhy)
            return Protected(protectedWhy);
        if (o.Group == ServiceGroup.Protected)
            return "It is a Windows service PoolWatch does not list, which it leaves alone.";
        // The gate itself. Only whether it throws matters here: the time goes into a record that is not kept.
        try { ServicePlan.BuildApply([s.Name], current.Options, current.InsiderEnrolled, Via, DateTime.UnixEpoch); }
        catch (InvalidOperationException ex) { return $"PoolWatch will not change it: {ex.Message}."; }
        return null;
    }

    private static string Protected(string why) => $"PoolWatch never changes it. It is a protected service: {why}.";

    /// <summary>What the review says of a name that is a startup app's or task's own name on this PC, but not the one PoolWatch's list gave it.</summary>
    internal const string NamedDifferently =
        "PoolWatch's list names it differently (with where it lives or its path, or with names hidden), so PoolWatch left it out. " +
        "Ask your agent to use the name the list gives.";

    /// <summary>
    /// Why a name matches nothing: a protected service that is not installed is still said to be protected, and a startup
    /// app or task that is on this PC under that name, but which the list named with its place or path, or with a name
    /// hidden, is not said to be missing (<see cref="NamedDifferently"/>). That is for the message only: such a name is never
    /// matched to an item.
    /// </summary>
    private static string NotFound(AgentItemKind kind, string id, AgentItemList current)
    {
        if (kind == AgentItemKind.Service && ServiceCatalogue.ProtectedReason(id, current.InsiderEnrolled) is { } why)
            return Protected(why);
        bool onThisPc = kind switch
        {
            AgentItemKind.Startup => current.Items.Any(i => i.Startup is { } s && s.Name.Equals(id, StringComparison.OrdinalIgnoreCase)),
            AgentItemKind.Task => current.Items.Any(i => i.Task is { } t &&
                                                         (t.Name.Equals(id, StringComparison.OrdinalIgnoreCase) || t.Path.Equals(id, StringComparison.OrdinalIgnoreCase))),
            _ => false,
        };
        if (onThisPc) return NamedDifferently;
        return kind switch
        {
            AgentItemKind.Service => "It is not a service on this PC now, so PoolWatch left it out.",
            AgentItemKind.Startup => "It is not one of this PC's startup apps now (Microsoft's are left alone), so PoolWatch left it out.",
            _ => "It is not one of this PC's scheduled tasks now (Microsoft's are left alone), so PoolWatch left it out.",
        };
    }
}
