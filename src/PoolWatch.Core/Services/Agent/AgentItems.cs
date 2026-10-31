using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Report;

namespace PoolWatch.Core.Services.Agent;

/// <summary>What an item on the AI-agent list is: the answer names each kind in its own list.</summary>
public enum AgentItemKind { Service, Startup, Task }

/// <summary>
/// A startup entry as the agent list takes it: the Tasks and startup apps tab's entry without its command line, which never
/// goes in the prompt. <paramref name="Location"/> is where it lives, as <see cref="StartupLocation"/> labels it ("HKCU
/// Run"); with <paramref name="Name"/> it is what the Disable path (the StartupApproved switch) needs.
/// <paramref name="Microsoft"/>: Microsoft's own entry, which PoolWatch leaves alone and the list leaves out.
/// <paramref name="Cost"/>: what its program costs now, or null when it is not running.
/// </summary>
public sealed record AgentStartupEntry(string Name, string Location, string? Publisher, bool Enabled, bool Microsoft, EntryCostResult? Cost = null);

/// <summary>
/// A scheduled task as the agent list takes it, without what it runs: its full path, which the Disable path (the task's
/// Enabled flag) needs, its name and its author. A task under \Microsoft is left out, as PoolWatch leaves those alone.
/// </summary>
public sealed record AgentScheduledTask(string Path, string Name, string? Author, bool Enabled, EntryCostResult? Cost = null);

/// <summary>
/// One thing on the AI-agent list. Its words (<see cref="Id"/> to <see cref="Source"/>) are the prompt's: redacted, plain
/// one-line text, bounded. What it is on this PC (<see cref="Service"/>, <see cref="Startup"/>, <see cref="Task"/>) never
/// goes in the prompt: the plan acts on that, never on a name from the answer.
/// </summary>
public sealed record AgentItem
{
    public required AgentItemKind Kind { get; init; }
    /// <summary>
    /// What the answer names it by, as the prompt shows it: the service's name; the startup entry's or task's name, or,
    /// when another item's reads the same there, the entry's name with where it lives, or the task's path
    /// (<see cref="ToldApart"/>).
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// <see cref="Id"/> carries where the startup entry lives, or is the task's path, because another item's name read the
    /// same in the prompt: the review then says where it lives too. The review also checks its own names, which it cuts
    /// shorter than ids, so that no two items of one kind it planned are shown as the same text (<see cref="AgentAnswer"/>); a
    /// name can still hold text that looks like a place, so the review shows the place in a line of its own.
    /// </summary>
    public bool ToldApart { get; init; }
    /// <summary>The name the list shows (a service's display name; for a startup entry or task, <see cref="Id"/>).</summary>
    public required string Shown { get; init; }
    /// <summary>
    /// Who it is from: "Windows" for a service Windows ships, else its publisher or the task's author, "&lt;ACCOUNT&gt;" for an
    /// account (<see cref="AgentItemList.Account"/>), or "unknown".
    /// </summary>
    public string From { get; init; } = "";
    /// <summary>
    /// What it costs now: a service as the Services tab's Cost column says it ("0.2 core", "idle", a shared host said so),
    /// or "unknown" when its process is not known; a startup entry or task "0.2 core", "idle", "already off" or "not running".
    /// </summary>
    public string Now { get; init; } = "";
    /// <summary>A service's start type ("Automatic", "Manual"); empty for a startup entry or task.</summary>
    public string Starts { get; init; } = "";
    /// <summary>What turning it off loses, in PoolWatch's words (a service the catalogue lists), else empty.</summary>
    public string Lose { get; init; } = "";
    /// <summary>The catalogue's note, else empty.</summary>
    public string Note { get; init; } = "";
    /// <summary>What the catalogue's source for it says (<see cref="ServiceSourceText"/>); empty for a startup entry or task.</summary>
    public string Source { get; init; } = "";
    /// <summary>In the prompt's lists. An item left out (a service not running, a protected one) may still be named: the plan's gate decides.</summary>
    public bool Listed { get; init; }

    public ServiceOption? Service { get; init; }
    public AgentStartupEntry? Startup { get; init; }
    public AgentScheduledTask? Task { get; init; }
}

/// <summary>
/// The things an AI agent may decide about, built from this PC as it is now: the services as the Services tab has them,
/// the startup entries and the scheduled tasks. The prompt lists some of them (<see cref="Listed"/>), and an answer is read
/// against this list built again when it is imported, so a name in the answer is only ever matched to something on the PC
/// now. Every word that goes in the prompt passes the issue report's redaction first (this user, machine, home and TEMP
/// paths, SIDs, other profiles, paths, serials, IP and MAC addresses), then loses control and formatting characters and is
/// cut to a bound, so a name can neither identify anyone nor break the prompt's layout.
/// </summary>
public sealed class AgentItemList
{
    /// <summary>The most services, and the most startup entries and tasks together, the prompt lists.</summary>
    public const int MaxListedServices = 100, MaxListedOthers = 100;

    /// <summary>The bounds on each word of an item, in characters.</summary>
    public const int MaxIdChars = 120, MaxNameChars = 100, MaxFromChars = 60, MaxLoseChars = 160, MaxNoteChars = 200;

    /// <summary>The bound on where a startup entry lives, when its name needs it ("HKCU Run").</summary>
    public const int MaxLocationChars = 40;

    /// <summary>
    /// The most characters of a word the redaction reads, far more than any word's bound: a program may name itself at any
    /// length (a Run value's name can be 16,383 characters), and the redaction's patterns stay quick on this many. A longer
    /// word is shown as <see cref="TooLong"/>, never in part.
    /// </summary>
    public const int MaxRawChars = 4096;

    /// <summary>What a service the catalogue does not list loses, as far as PoolWatch knows.</summary>
    public const string UnknownLose = "not known to PoolWatch";

    /// <summary>
    /// Who an item is from when that is an account (DOMAIN\name, PC\name): a scheduled task's author usually is one, and it
    /// may be another person's on this PC or network, which the redaction cannot know.
    /// </summary>
    public const string Account = "<ACCOUNT>";

    public IReadOnlyList<AgentItem> Items { get; }
    /// <summary>The services the prompt lists, in its order: Windows first, then by name.</summary>
    public IReadOnlyList<AgentItem> ListedServices { get; }
    /// <summary>The startup entries, then the tasks, the prompt lists, each by name.</summary>
    public IReadOnlyList<AgentItem> ListedOthers { get; }
    /// <summary>How many more running optional services, and startup entries and tasks, there are than the prompt lists.</summary>
    public int ServicesNotListed { get; }
    public int OthersNotListed { get; }
    /// <summary>The services as given, for the plan's gate.</summary>
    public IReadOnlyList<ServiceOption> Options { get; }
    public bool InsiderEnrolled { get; }
    public RedactionInfo Redaction { get; }

    private AgentItemList(List<AgentItem> items, IReadOnlyList<ServiceOption> options, bool insider, RedactionInfo redaction,
        List<AgentItem> services, int servicesLeft, List<AgentItem> others, int othersLeft)
    {
        Items = items;
        Options = options;
        InsiderEnrolled = insider;
        Redaction = redaction;
        ListedServices = services;
        ServicesNotListed = servicesLeft;
        ListedOthers = others;
        OthersNotListed = othersLeft;
    }

    /// <summary>The items of <paramref name="kind"/> the answer's <paramref name="id"/> names (any case): none, one, or more when it is ambiguous.</summary>
    public List<AgentItem> Find(AgentItemKind kind, string id) =>
        Items.Where(i => i.Kind == kind && i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The list for this PC. <paramref name="options"/>: the Services tab's services (<see cref="ServicePlan.Options"/>,
    /// with the Insider rule already applied); every one becomes an item, and the prompt lists those that may be turned
    /// off, are Win32 services (never a driver), are running and are not off already. <paramref name="startup"/>: every
    /// startup entry; Microsoft's are left out. <paramref name="tasks"/>: the scheduled tasks that are not Microsoft's; one
    /// under \Microsoft is left out too.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">
    /// The redaction gave up on a word (its patterns are bounded in time, and each word in length, so this is not expected).
    /// The list is not built rather than holding a word that was not redacted; the caller says so plainly.
    /// </exception>
    public static AgentItemList Build(IReadOnlyList<ServiceOption> options, IEnumerable<AgentStartupEntry> startup, IEnumerable<AgentScheduledTask> tasks,
        bool insiderEnrolled, RedactionInfo redaction)
    {
        var items = new List<AgentItem>();
        foreach (var o in options)
        {
            var s = o.Service;
            bool listed = o.CanTurnOff && s.IsWin32 && s.Running && !ServicePlan.IsOff(o);
            items.Add(new AgentItem
            {
                Kind = AgentItemKind.Service,
                Id = Field(s.Name, MaxIdChars, redaction),
                Shown = Field(s.DisplayName.Length > 0 ? s.DisplayName : s.Name, MaxNameChars, redaction),
                From = ServiceFrom(o, redaction),
                Now = ServicePlan.CostText(s) is { Length: > 0 } cost ? cost : "unknown",
                Starts = s.StartType == ServiceStartType.Unknown ? "unknown" : s.StartType.ToString(),
                Lose = o.Catalogue is { } c ? Field(c.Lose, MaxLoseChars, redaction) : UnknownLose,
                Note = Field(o.Catalogue?.Note, MaxNoteChars, redaction),
                Source = ServiceSourceText.Label(o.Catalogue?.Source.Kind ?? ServiceSourceKind.None),
                Listed = listed,
                Service = o,
            });
        }

        // Two names are told apart when they read the same in the prompt (redacted and cut), not only when they are the same:
        // names that differ only in what the redaction hides would otherwise both be listed, and refused, under one id.
        // A startup entry is told apart by where it lives; two in one place that still read the same stay ambiguous, and an
        // answer naming them is refused.
        var entries = startup.Where(e => e is { Microsoft: false }).ToList();
        var entryIds = Ids(entries.Select(e => Field(e.Name, MaxIdChars, redaction)).ToList(),
            i => Field(entries[i].Location, MaxLocationChars, redaction) is { Length: > 0 } where
                ? $"{Field(entries[i].Name, MaxIdChars - where.Length - 3, redaction)} ({where})" : null);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            items.Add(new AgentItem
            {
                Kind = AgentItemKind.Startup, Id = entryIds[i].Id, Shown = entryIds[i].Id, ToldApart = entryIds[i].ToldApart,
                From = From(e.Publisher, redaction), Now = OtherNow(e.Enabled, e.Cost), Listed = true, Startup = e,
            });
        }

        // A task is named by its name; one whose name another's reads the same as, by its path (unique in Task Scheduler).
        var jobs = tasks.Where(t => !t.Path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase)).ToList();
        var jobIds = Ids(jobs.Select(t => Field(t.Name, MaxIdChars, redaction)).ToList(), i => Field(jobs[i].Path, MaxIdChars, redaction));
        for (int i = 0; i < jobs.Count; i++)
        {
            var t = jobs[i];
            items.Add(new AgentItem
            {
                Kind = AgentItemKind.Task, Id = jobIds[i].Id, Shown = jobIds[i].Id, ToldApart = jobIds[i].ToldApart,
                From = From(t.Author, redaction), Now = OtherNow(t.Enabled, t.Cost), Listed = true, Task = t,
            });
        }

        var byName = StringComparer.OrdinalIgnoreCase;
        var serviceOrder = Enumerable.Range(0, items.Count).Where(i => items[i] is { Kind: AgentItemKind.Service, Listed: true })
            .OrderBy(i => items[i].From == WindowsFrom ? 0 : 1).ThenBy(i => items[i].Shown, byName).ThenBy(i => items[i].Id, byName).ToList();
        var otherOrder = Enumerable.Range(0, items.Count).Where(i => items[i].Kind != AgentItemKind.Service)
            .OrderBy(i => items[i].Kind).ThenBy(i => items[i].Shown, byName).ThenBy(i => items[i].Id, byName).ToList();
        // What the caps leave out is not listed. It can still be named, and the plan decides on it as on any other.
        foreach (int i in serviceOrder.Skip(MaxListedServices).Concat(otherOrder.Skip(MaxListedOthers)))
            items[i] = items[i] with { Listed = false };
        return new AgentItemList(items, options, insiderEnrolled, redaction,
            serviceOrder.Take(MaxListedServices).Select(i => items[i]).ToList(), Math.Max(0, serviceOrder.Count - MaxListedServices),
            otherOrder.Take(MaxListedOthers).Select(i => items[i]).ToList(), Math.Max(0, otherOrder.Count - MaxListedOthers));
    }

    /// <summary>
    /// The ids of items of one kind: each its <paramref name="names"/> entry, until it reads the same as another's (in any
    /// case); then its <paramref name="other"/> id (with where it lives, or its path), when it has one, and it is told
    /// apart. That is checked again until nothing changes, since an other id can read like a name ("Foo (HKCU Run)" as one
    /// entry's name, and as another "Foo"'s with its place). Each item is looked at once, so this ends; ids that still read
    /// the same stay ambiguous, and an answer naming one is refused.
    /// </summary>
    private static (string Id, bool ToldApart)[] Ids(IReadOnlyList<string> names, Func<int, string?> other)
    {
        var ids = names.ToArray();
        var done = new bool[ids.Length];
        var apart = new bool[ids.Length];
        for (bool changed = true; changed;)
        {
            changed = false;
            var counts = Counts(ids);
            for (int i = 0; i < ids.Length; i++)
            {
                if (done[i] || counts[ids[i]] < 2) continue;
                done[i] = true;
                if (other(i) is not { } id) continue;
                ids[i] = id;
                apart[i] = changed = true;
            }
        }
        return ids.Select((id, i) => (id, apart[i])).ToArray();
    }

    /// <summary>How many times each name occurs, in any case.</summary>
    internal static Dictionary<string, int> Counts(IEnumerable<string> names)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) counts[n] = counts.GetValueOrDefault(n) + 1;
        return counts;
    }

    /// <summary>The From of a service Windows ships (Microsoft's, with a Windows default start type in the catalogue).</summary>
    public const string WindowsFrom = "Windows";

    private static string ServiceFrom(ServiceOption o, RedactionInfo redaction)
    {
        if (o.Service.MicrosoftSigned && o.WindowsDefault is not null) return WindowsFrom;
        if (Who(o.Service.Publisher, redaction) is { Length: > 0 } p) return p;
        return o.Service.MicrosoftSigned ? "Microsoft" : "unknown";
    }

    /// <summary>A startup entry's publisher or a task's author as the prompt says it (<see cref="Who"/>), or "unknown".</summary>
    private static string From(string? raw, RedactionInfo redaction) => Who(raw, redaction) is { Length: > 0 } p ? p : "unknown";

    /// <summary>
    /// A publisher's or author's name as the prompt may say it: redacted and cut, or empty. A name that is still an account
    /// after the redaction (it holds a backslash: DOMAIN\name, PC\name, another person's included) says only
    /// <see cref="Account"/>. The check reads the whole name before the cut, so a long domain cannot push the backslash out of it.
    /// </summary>
    private static string Who(string? raw, RedactionInfo redaction)
    {
        string text = Field(raw, MaxRawChars, redaction);
        return text.Contains('\\') ? Account : Cut(text, MaxFromChars);
    }

    private static string OtherNow(bool enabled, EntryCostResult? cost) =>
        !enabled ? "already off" : cost is { } c ? ServicePlan.Cost(c.Cores, c.PageFaultsPerSec) : "not running";

    /// <summary>
    /// A word as the prompt may hold it: one plain line (control characters become spaces, formatting characters such as
    /// bidirectional marks and zero-width spaces go, runs of spaces become one), <see cref="TooLong"/> when that is longer
    /// than <see cref="MaxRawChars"/>, then the issue report's redaction, a folder path that is still there (the issue
    /// report keeps those under Windows and Program Files) hidden from where it starts, a table's "|" and a code span's
    /// backtick swapped for "/" and "'", and at most <paramref name="max"/> characters, cut with "...". Formatting
    /// characters go before the redaction, so one cannot hide a name from it; the names the redaction looks for are made
    /// plain the same way, so a user name with two spaces running, say, still matches. Empty for null.
    /// </summary>
    public static string Field(string? raw, int max, RedactionInfo redaction)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var plain = Plainly(redaction);
        string text = Glued(IssueReport.Redact(Bounded(Plain(raw)), plain), plain);
        text = DrivePath.Replace(Plain(text), "<PATH>").Replace('|', '/').Replace('`', '\'');
        return Cut(text, max);
    }

    /// <summary>What a word longer than <see cref="MaxRawChars"/> says instead, all of it.</summary>
    public const string TooLong = "<TOO LONG>";

    /// <summary>
    /// A plain line for the redaction to read whole, or <see cref="TooLong"/> when it is longer than
    /// <see cref="MaxRawChars"/>. Never a part of it: a cut anywhere could fall inside a name the redaction matches only
    /// whole (a user name with a space in it, "DOMAIN\name"), and leave its first part in the prompt. A word that long is
    /// one a program built on purpose, and at most <see cref="MaxNoteChars"/> characters of any word are ever shown.
    /// </summary>
    internal static string Bounded(string s) => s.Length <= MaxRawChars ? s : TooLong;

    /// <summary>The redaction's names as <see cref="Plain"/> leaves them in a word, so that the word and the name still match.</summary>
    private static RedactionInfo Plainly(RedactionInfo r) => r with
    {
        UserName = r.UserName is { } user ? Plain(user) : r.UserName,
        MachineName = r.MachineName is { } machine ? Plain(machine) : r.MachineName,
        HomePaths = r.HomePaths is { } homes ? homes.Select(Plain).ToList() : r.HomePaths,
        Domain = r.Domain is { } domain ? Plain(domain) : null,
        TempPaths = r.TempPaths?.Select(Plain).ToList(),
    };

    /// <summary>
    /// A backstop for what the issue report's redaction matches only as a whole word: a program can glue a SID, the user's
    /// name or the PC's name into its own ("ExampleUpdaterTaskUserS-1-5-21-..."), and an address can follow a name the
    /// redaction already swapped ("&lt;USER&gt;@example.com"). A user name that is also an ordinary word, or shorter than four
    /// characters, is left to the redaction, which hides it only as a path segment; so is a machine name shorter than four.
    /// </summary>
    private static string Glued(string text, RedactionInfo r)
    {
        text = AnySid.Replace(text, "<SID>");
        text = AnyEmail.Replace(text, "<EMAIL>");
        if (r.MachineName is { Length: >= 4 } machine)
            text = Regex.Replace(text, Regex.Escape(machine), "<MACHINE>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (r.UserName is { Length: >= 4 } user && !CommonWords.Contains(user))
            text = Regex.Replace(text, Regex.Escape(user), "<USER>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return text;
    }

    /// <summary>Account names that are also ordinary words (as the export redaction keeps them).</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
        { "user", "users", "admin", "administrator", "owner", "test", "guest", "dev", "default", "public", "system" };

    private static readonly Regex AnySid = new(@"S-1-5-21(?:-\d+){3,4}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex AnyEmail = new(@"[^\s@/|]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A folder path to the end of the text: a drive path (C:\..., D:/...), a network share, or what the redaction left of
    /// a path in a profile or TEMP. A name that holds one says no more after it.
    /// </summary>
    private static readonly Regex DrivePath = new(@"(?:(?<![A-Za-z0-9])[A-Za-z]:[\\/]|\\\\|<HOME>|<TEMP>|<SHARE>).*$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>One line: control characters as spaces, formatting characters left out, whitespace runs as one space, trimmed.</summary>
    internal static string Plain(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char ch in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.Format) continue;
            bool white = char.IsWhiteSpace(ch) || cat is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            if (white) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>At most <paramref name="max"/> characters, "..." included, never cutting a surrogate pair.</summary>
    internal static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        int end = Math.Max(0, max - 3);
        if (end > 0 && char.IsHighSurrogate(s[end - 1])) end--;
        return s[..end].TrimEnd() + "...";
    }
}
