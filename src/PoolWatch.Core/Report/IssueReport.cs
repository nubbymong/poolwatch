using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoolWatch.Core.Correlation;
using PoolWatch.Core.Detection;
using PoolWatch.Core.Incidents;
using PoolWatch.Core.Util;

namespace PoolWatch.Core.Report;

/// <summary>
/// What a GitHub issue says about this PC (Report an issue): facts PoolWatch already holds, each one plain text. Never a
/// command line, an evidence line, a path of another app, a window title, a Claude Code or AI Code Conductor session,
/// project or account name, the services list or the environment.
/// </summary>
/// <param name="Install">How this copy was installed, in the issue form's own words ("installer (setup.exe)").</param>
/// <param name="Windows">Name, display version, build and Insider enrolment: "Windows 11 Pro 25H2, build 26200.8655".</param>
/// <param name="KernelMemory">The committed pool now and its change over the last hour: "9.6 GB, up 1.2 GB in the last hour".</param>
/// <param name="TopTags">The largest pool tags with their plain names and sizes, biggest first.</param>
/// <param name="Incident">An incident's digest (<see cref="IssueReport.IncidentDigest"/>), or null.</param>
public sealed record IssueFacts(string Version, string Install, string Windows, string Architecture, long RamBytes, int DevDrives,
    string KernelMemory, string AutoClear, string TokenGuard, string OwnCost, IReadOnlyList<string> TopTags, string? Incident);

/// <summary>
/// Report an issue: the text that goes into a new GitHub issue, its redaction, and the address that opens the issue form
/// with its short fields filled in. Pure: nothing here sends anything (the user pastes the text and submits the form).
/// </summary>
public static class IssueReport
{
    public const string NewIssueUrl = "https://github.com/nubbymong/poolwatch/issues/new";
    public const string AdvisoryUrl = "https://github.com/nubbymong/poolwatch/security/advisories/new";

    /// <summary>
    /// The longest address handed to explorer.exe: Explorer reads its argument into a MAX_PATH (260-character) buffer
    /// and cuts anything longer, so the address stays under that, with a few characters to spare.
    /// </summary>
    public const int MaxUrlLength = 255;

    // ------------------------------------------------------------------ the text

    /// <summary>The facts as plain lines (no tables: the issue shows them as they are).</summary>
    public static string Details(IssueFacts f)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"PoolWatch {f.Version}, {f.Install}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Windows: {f.Windows}, {f.Architecture}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Memory: {Fmt.Bytes(f.RamBytes)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Dev Drives: {f.DevDrives}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Kernel pool now: {f.KernelMemory}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Automatic cache clear: {f.AutoClear}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Token-leak guard: {f.TokenGuard}\n");
        sb.Append(f.OwnCost).Append('\n');
        if (f.TopTags.Count > 0)
        {
            sb.Append("Largest kernel pool tags:\n");
            foreach (var t in f.TopTags) sb.Append("- ").Append(t).Append('\n');
        }
        if (f.Incident is { Length: > 0 } incident) sb.Append('\n').Append(incident).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// "Windows 11 Pro 25H2, build 26200.8655, Insider" from the registry's product name and display version, which say
    /// "Windows 10" on Windows 11: a build from 22000 on is Windows 11.
    /// </summary>
    public static string WindowsLine(string productAndVersion, string build, bool insider)
    {
        string name = productAndVersion.Trim();
        bool eleven = int.TryParse(build.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) && major >= 22000;
        if (eleven && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)) name = "Windows 11" + name["Windows 10".Length..];
        return $"{name}, build {build}" + (insider ? ", Insider" : "");
    }

    /// <summary>Everything the issue gets, as the preview shows it and Copy all and Save as file write it.</summary>
    public static string FullText(string title, string what, string details)
    {
        var sb = new StringBuilder(title.Trim()).Append("\n\n");
        if (what.Trim() is { Length: > 0 } w) sb.Append("What happened\n").Append(Lines(w)).Append("\n\n");
        if (details.Trim() is { Length: > 0 } d) sb.Append("What PoolWatch saw\n").Append(Lines(d)).Append('\n');
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    // ------------------------------------------------------------------ redaction

    /// <summary>
    /// The export redaction first (secrets, then this user, machine, home and TEMP paths, domain\user, SID), then generic
    /// rules as a backstop for what it cannot know: other accounts' SIDs, the domain after the machine name, network
    /// shares, another account's profile folder, the folders below a profile's standard ones, any other absolute path
    /// outside Windows, Program Files and PoolWatch's own data folder, a work OneDrive's organisation, GUIDs, volume
    /// serials, e-mail, IP and MAC addresses.
    /// </summary>
    public static string Redact(string text, RedactionInfo r)
    {
        text = Redactor.Redact(text, r);
        text = Sids.Replace(text, "<SID>");
        text = MachineDomains.Replace(text, "<MACHINE>.<DOMAIN>");
        text = Guids.Replace(text, "<ID>");
        text = Shares.Replace(text, m => Hide(m, "<SHARE>"));
        text = ProfileFolders.Replace(text, m => m.Groups["root"].Value + "<USER>");
        text = ProfilePaths.Replace(text, HideProfileTail);
        text = OtherPaths.Replace(text, m => m.Length <= 3 ? m.Value : Hide(m, "<PATH>")); // a drive root says nothing about anyone
        text = OneDriveOrganisations.Replace(text, m => Hide(m, "OneDrive - <ORG>"));
        text = Emails.Replace(text, "<EMAIL>");
        text = MacAddresses.Replace(text, "<MAC>");
        text = IPv6Addresses.Replace(text, "<IP>");
        text = IPv4Addresses.Replace(text, "<IP>");
        return Serials.Replace(text, "<SERIAL>");
    }

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(10);

    // A character a Windows file or folder name may hold (not a line break), and one that is not a space either.
    private const string NameChar = @"[^\\/:*?""<>|\r\n]";
    private const string WordChar = @"[^\\/:*?""<>|\r\n\s]";

    // The rest of a path after its root: folders, which may hold spaces (each ends at a separator), then the last name. That
    // ends at a space unless the next word looks like more of the name: a word of two letters or more that starts with a
    // capital ("Project Falcon"), a number ("Build 2") or a file name ("My File.txt"), and never after sentence punctuation.
    private const string PathTail =
        $@"(?:{NameChar}+?[\\/])*{WordChar}*(?:(?<=[^\s.,;:)\]'""]) (?=(?-i:[A-Z]){WordChar}|[0-9]|{WordChar}*?\.[A-Za-z0-9]{{1,8}}(?:[\s.,;)\]'""]|$)){WordChar}+)*";

    // An account's SID (S-1-5-21-<machine or domain>-<account>): the machine or domain part identifies the PC.
    private static readonly Regex Sids = new(@"(?<![\w-])S-1-5-21(?:-\d+){3,4}(?![\w-])", Options, MatchTimeout);

    // The DNS suffix after a redacted machine name (<MACHINE>.corp.contoso.com).
    private static readonly Regex MachineDomains = new(@"<MACHINE>(?:\.[A-Za-z0-9-]+)+", Options, MatchTimeout);

    private static readonly Regex Guids = new(@"(?:Volume)?\{?\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b\}?",
        Options | RegexOptions.IgnoreCase, MatchTimeout);

    // \\server\share\... (the server may already be <MACHINE>); not a device path (\\?\ or \\.\).
    private static readonly Regex Shares = new(
        $@"(?<![\\\w])\\\\(?![?.]\\)(?:<MACHINE>(?:\.<DOMAIN>)?|[^\\/\s""'<>|*?:]+)\\{PathTail}", Options, MatchTimeout);

    // X:\Users\<name>: the name runs to the next separator (it may hold spaces), else to a space.
    private static readonly Regex ProfileFolders = new(
        $@"(?<![A-Za-z0-9])(?<root>[A-Za-z]:[\\/]Users[\\/])(?:{NameChar}+?(?=[\\/])|{WordChar}+)", Options | RegexOptions.IgnoreCase, MatchTimeout);

    // A path inside a profile once its owner is hidden: <HOME>, <TEMP> or X:\Users\<USER>, then the rest.
    private static readonly Regex ProfilePaths = new(
        $@"(?<head><HOME>|<TEMP>|(?<![A-Za-z0-9])[A-Za-z]:[\\/]Users[\\/]<USER>)(?<tail>[\\/]{PathTail})?", Options | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>A profile's standard folders: they stay in a path below a profile, and what is below them is hidden.</summary>
    private static readonly HashSet<string> ProfileFolderNames = new(StringComparer.OrdinalIgnoreCase)
        { "AppData", "Desktop", "Documents", "Downloads", "Favorites", "Music", "OneDrive", "Pictures", "Saved Games", "source", "Videos" };

    private static readonly HashSet<string> AppDataFolders = new(StringComparer.OrdinalIgnoreCase) { "Local", "LocalLow", "Roaming" };

    /// <summary>
    /// "&lt;HOME&gt;\source\repos\app\x.cs" becomes "&lt;HOME&gt;\source\&lt;PATH&gt;": a standard folder stays (and AppData's
    /// Local, LocalLow or Roaming), a work OneDrive loses its organisation, and the folders and files below say what the
    /// person works on, so they are hidden.
    /// </summary>
    private static string HideProfileTail(Match m)
    {
        string path = WithoutPunctuation(m.Groups["tail"].Value, out string after);
        if (path.Length <= 1) return m.Value;
        char separator = path[0];
        string[] names = path[1..].Split('\\', '/');
        int keep = 0;
        if (ProfileFolderNames.Contains(names[0]))
            keep = names[0].Equals("AppData", StringComparison.OrdinalIgnoreCase) && names.Length > 1 && AppDataFolders.Contains(names[1]) ? 2 : 1;
        else if (names[0].StartsWith("OneDrive - ", StringComparison.OrdinalIgnoreCase))
        {
            names[0] = "OneDrive - <ORG>";
            keep = 1;
        }
        string shown = names.Skip(keep).Any(n => n.Length > 0)
            ? string.Join(separator, names.Take(keep).Append("<PATH>"))
            : string.Join(separator, names);
        return m.Groups["head"].Value + separator + shown + after;
    }

    /// <summary>The folders whose paths stay: Windows, Program Files (both) and PoolWatch's own data folder.</summary>
    private static readonly string[] KeptRoots =
    [
        Env("SystemRoot", @"C:\Windows"), Env("ProgramFiles", @"C:\Program Files"), Env("ProgramFiles(x86)", @"C:\Program Files (x86)"),
        Env("ProgramData", @"C:\ProgramData") + @"\PoolWatch",
    ];

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 3 } v ? v.TrimEnd('\\') : fallback;

    // Any other X:\folder\...\name. A path that starts with a kept folder (the folder itself, not C:\Windows.old or
    // C:\ProgramData\PoolWatch-x) is not matched at all, and X:\Users\ is the rules above.
    private static readonly Regex OtherPaths = new(
        $@"(?<![A-Za-z0-9])(?!(?:{string.Join("|", KeptRoots.Select(k => Regex.Escape(k).Replace(@"\\", @"[\\/]")))})(?=[\\/]|$|[\s""'),;]|\.(?!\w)))" +
        $@"[A-Za-z]:[\\/](?!Users\b){PathTail}", Options | RegexOptions.IgnoreCase, MatchTimeout);

    // A work or school OneDrive is named after its organisation ("OneDrive - Contoso Ltd"); the name's words run on while
    // they start with a capital letter, a digit or "&".
    private static readonly Regex OneDriveOrganisations = new(
        $@"\bOneDrive - {WordChar}+(?: (?-i:[A-Z0-9&]){WordChar}*)*", Options | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>The placeholder, then the sentence punctuation the match ended with (not part of the path or name).</summary>
    private static string Hide(Match m, string placeholder)
    {
        WithoutPunctuation(m.Value, out string after);
        return placeholder + after;
    }

    private static string WithoutPunctuation(string path, out string after)
    {
        string trimmed = path.TrimEnd('.', ',', ';', ')', ']', '\'');
        after = path[trimmed.Length..];
        return trimmed;
    }

    private static readonly Regex Emails = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", Options, MatchTimeout);

    private static readonly Regex MacAddresses = new(@"(?<![0-9a-f:\-])(?:[0-9a-f]{2}[:\-]){5}[0-9a-f]{2}(?![0-9a-f:\-])", Options | RegexOptions.IgnoreCase, MatchTimeout);

    // The full form, or a compressed one (with "::"), so a clock time such as 09:12:30 is never taken for one.
    private static readonly Regex IPv6Addresses = new(
        @"(?<![\w:.])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?|::[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})(?:%\w+)?(?![\w:])",
        Options | RegexOptions.IgnoreCase, MatchTimeout);

    private const string Octet = @"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";
    private static readonly Regex IPv4Addresses = new($@"(?<!\d|\d\.)(?:{Octet}\.){{3}}{Octet}(?!\.?\d)", Options, MatchTimeout);

    // A volume serial as Windows prints it (1A2B-3C4D): upper case only, so ordinary hyphenated words stay, and with a
    // letter in it, so a range such as 2024-2025 stays too.
    private static readonly Regex Serials = new(
        @"(?<![0-9A-Fa-f\-])(?=[0-9]{0,3}[A-F]|[0-9A-F]{4}-[0-9]{0,3}[A-F])[0-9A-F]{4}-[0-9A-F]{4}(?![0-9A-Fa-f\-])", Options, MatchTimeout);

    // ------------------------------------------------------------------ the address

    /// <summary>
    /// The address of a new issue with the bug form's short fields filled in (field ids <c>title</c>, <c>version</c>,
    /// <c>windows</c>), every value escaped, at most <see cref="MaxUrlLength"/> characters. The description and the
    /// evidence never fit in that: the user pastes the full text (<see cref="FullText"/>). The version and Windows go in
    /// whole or not at all, since half a build number would mislead; the title takes the room left, cut at a space.
    /// </summary>
    public static string Url(string title, string version, string windows)
    {
        const string start = NewIssueUrl + "?template=bug_report.yml", titleKey = "&title=";
        string fields = "";
        foreach (var (id, value) in new[] { ("version", version), ("windows", windows) })
        {
            string field = $"&{id}={E(FirstLine(value))}";
            if (field.Length > id.Length + 2 && start.Length + titleKey.Length + fields.Length + field.Length <= MaxUrlLength) fields += field;
        }
        return start + titleKey + E(Shorten(FirstLine(title), MaxUrlLength - start.Length - titleKey.Length - fields.Length)) + fields;
    }

    private static string E(string s) => Uri.EscapeDataString(s);

    private static string Lines(string s) => s.ReplaceLineEndings("\n");

    private static string FirstLine(string s) => Lines(s).Split('\n')[0].Trim();

    /// <summary>
    /// <paramref name="line"/> when its escaped form takes at most <paramref name="budget"/> characters; else its longest
    /// start that fits with "..." after it, cut at a space when there is one in its second half, never inside a surrogate
    /// pair. Escaping is per character, so the lengths of the parts add up.
    /// </summary>
    private static string Shorten(string line, int budget)
    {
        if (E(line).Length <= budget) return line;
        const string more = "...";
        int used = more.Length, end = 0;
        while (end < line.Length)
        {
            int n = char.IsHighSurrogate(line[end]) && end + 1 < line.Length ? 2 : 1;
            int cost = E(line.Substring(end, n)).Length;
            if (used + cost > budget) break;
            used += cost;
            end += n;
        }
        if (end == 0) return "";
        int space = line.LastIndexOf(' ', end - 1);
        return line[..(space > end / 2 ? space : end)].TrimEnd() + more;
    }

    // ------------------------------------------------------------------ an incident

    /// <summary>
    /// An incident's report.json in a few lines: its headline, the plain answer and the top three suspects with their
    /// confidence. Nothing else from the file: no command line, evidence line, path or window title. A suspect's role (which
    /// can quote a script or a command) is left out, and a Claude Code session is named without its project folder or id.
    /// Null when the file is not an incident report.
    /// </summary>
    public static string? IncidentDigest(string reportJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(reportJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Text(r, "headline") is not { Length: > 0 } headline) return null;
            var suspects = new List<(string Display, string Public, string Confidence)>();
            if (r.TryGetProperty("suspects", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var s in list.EnumerateArray().Take(3))
                    if (s.ValueKind == JsonValueKind.Object && Text(s, "displayName") is { Length: > 0 } display)
                        suspects.Add((display, PublicName(display, Text(s, "image")), ConfidenceOf(s)));

            // The headline names the top suspect by its full display name: swap in the public one. When that name is not in
            // it as written (an older report), the suspect is left out of the headline: the suspects line names it.
            if (suspects.Count > 0 && headline.Contains(suspects[0].Display, StringComparison.Ordinal))
                headline = headline.Replace(suspects[0].Display, suspects[0].Public, StringComparison.Ordinal);
            else if (headline.IndexOf(TopSuspect, StringComparison.Ordinal) is int at and >= 0) headline = headline[..at];
            var sb = new StringBuilder();
            string kind = Text(r, "kind") switch
            {
                nameof(IncidentKind.Growth) => "growth", nameof(IncidentKind.NonPagedTotal) => "non-paged pool over its limit",
                nameof(IncidentKind.Release) => "release", nameof(IncidentKind.Capture) => "capture", _ => "incident",
            };
            sb.Append(CultureInfo.InvariantCulture, $"Incident {Text(r, "id") ?? "?"} ({kind}): {Sessions(headline).TrimEnd('.')}.\n");
            if (Text(r, "plainAnswer") is { Length: > 0 } answer) sb.Append(Sessions(answer)).Append('\n');
            if (suspects.Count > 0) sb.Append("Top suspects: ").Append(string.Join("; ", suspects.Select(s => $"{s.Public} ({s.Confidence})"))).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ConfidenceOf(JsonElement suspect)
    {
        if (!suspect.TryGetProperty("confidence", out var c)) return "confidence not recorded";
        if (c.ValueKind == JsonValueKind.String && Enum.TryParse<Confidence>(c.GetString(), out var named)) return PlainWords.Confidence(named);
        if (c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) && Enum.IsDefined((Confidence)n)) return PlainWords.Confidence((Confidence)n);
        return "confidence not recorded";
    }

    /// <summary>Where the headline names its top suspect (IncidentWriter.Headline).</summary>
    private const string TopSuspect = "; top suspect ";

    /// <summary>
    /// A suspect's name for the issue: its image, its services, and who it works for when nothing stands between
    /// ("node.exe under Codex", "svchost.exe (WSearch)"). A role or a label can quote a command line ("pwsh.exe ·
    /// command: ...", "node.exe [command: ...]") and nothing marks where it ends, so it goes whole, and only an AI tool's
    /// own name may follow it: "node.exe · command: npm run x under Claude Code session app (PID 7)" becomes "node.exe
    /// under Claude Code session (PID 7)". A name that does not start with the image is replaced by the image.
    /// </summary>
    private static string PublicName(string display, string? image)
    {
        if (image is not { Length: > 0 } || !display.StartsWith(image, StringComparison.Ordinal)) return image is { Length: > 0 } ? image : "unknown process";
        string rest = display[image.Length..];
        if (rest.StartsWith(" · ", StringComparison.Ordinal))
            return image + (AiToolOwner.Match(rest) is { Success: true } m ? " under " + Sessions(m.Groups["owner"].Value) : "");
        int label = rest.IndexOf(" [", StringComparison.Ordinal);
        return Sessions(image + (label >= 0 ? rest[..label] : rest));
    }

    // The owner a display name ends with, when it is an AI tool: AI Code Conductor, or a Claude Code session and its PID.
    private static readonly Regex AiToolOwner = new(
        @" under (?<owner>AI Code Conductor|Claude Code session [^\r\n]*?\(PID \d+\))(?: \(launched via WMI\))?$", Options, MatchTimeout);

    // The headline adds "by Claude Code session <id> / workflow <id>" to the top suspect; a session's name holds its project folder.
    private static readonly Regex SessionReference = new(@" by Claude Code session \S+(?: / (?:workflow|sub-agent) \S+)?", Options, MatchTimeout);
    private static readonly Regex SessionName = new(@"Claude Code session (?!\(PID)[^\r\n]*?\(PID", Options, MatchTimeout);

    private static string Sessions(string text) => SessionName.Replace(SessionReference.Replace(text, ""), "Claude Code session (PID");
}
