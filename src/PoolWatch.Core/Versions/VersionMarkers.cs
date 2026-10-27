using System.Text.RegularExpressions;
using PoolWatch.Core.Findings;

namespace PoolWatch.Core.Versions;

/// <summary>
/// App versions on the timeline: Store packages (ChatGPT, Codex: package full names from the per-user AppModel
/// repository key), the Claude Code versions installed under <c>%USERPROFILE%\.local\share\claude\versions</c> (one
/// folder listing), and the AI Code Conductor exe's file version. A change against the previous check (kept across
/// runs) is a finding. Explicit locations only, never a search.
/// </summary>
public static partial class VersionMarkers
{
    /// <summary>Store packages whose versions are tracked: package family name prefixes.</summary>
    public static readonly IReadOnlyList<(string prefix, string name)> WatchedPackages =
        [("OpenAI.ChatGPT", "ChatGPT"), ("OpenAI.Codex", "Codex"), ("Claude", "Claude"), ("Anthropic", "Claude")];

    /// <summary>"OpenAI.ChatGPT-Desktop_1.2025.112.0_x64__2p2nqsd0c76g0" to ("ChatGPT", "1.2025.112.0"); null for other packages.</summary>
    public static (string name, string version)? FromPackageFullName(string fullName)
    {
        var m = PackageFullName().Match(fullName);
        if (!m.Success) return null;
        string family = m.Groups["n"].Value;
        foreach (var (prefix, name) in WatchedPackages)
            if (family.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (name, m.Groups["v"].Value);
        return null;
    }

    /// <summary>The newest version among the Claude Code version folder names ("2.0.14", "2.0.15"); null when none parse.</summary>
    public static string? NewestClaudeCode(IEnumerable<string> folderNames)
    {
        Version? best = null;
        string? bestName = null;
        foreach (var n in folderNames)
        {
            var m = VersionPrefix().Match(n);
            if (!m.Success || !Version.TryParse(m.Value, out var v)) continue;
            if (best is null || v > best) { best = v; bestName = n; }
        }
        return bestName;
    }

    /// <summary>Changes from <paramref name="previous"/> to <paramref name="current"/> (name to version), as findings.</summary>
    public static List<Finding> Diff(IReadOnlyDictionary<string, string>? previous, IReadOnlyDictionary<string, string> current, DateTime nowUtc)
    {
        var found = new List<Finding>();
        if (previous is null)
        {
            if (current.Count > 0)
                found.Add(new Finding
                {
                    TimeUtc = nowUtc, Kind = FindingKind.VersionChange, Title = "App versions: " + string.Join(", ", current.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")),
                    Data = current.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                });
            return found;
        }
        foreach (var (name, v) in current.OrderBy(kv => kv.Key))
        {
            if (!previous.TryGetValue(name, out var old))
                found.Add(Change(nowUtc, name, null, v, $"{name} {v} installed"));
            else if (old != v)
                found.Add(Change(nowUtc, name, old, v, $"{name} updated {old} -> {v}"));
        }
        foreach (var (name, old) in previous.OrderBy(kv => kv.Key))
            if (!current.ContainsKey(name)) found.Add(Change(nowUtc, name, old, null, $"{name} {old} no longer found"));
        return found;
    }

    private static Finding Change(DateTime t, string name, string? from, string? to, string title) => new()
    {
        TimeUtc = t, Kind = FindingKind.VersionChange, Severity = FindingSeverity.Warning, Title = "Version change: " + title,
        Data = new Dictionary<string, object?> { ["app"] = name, ["from"] = from, ["to"] = to },
    };

    /// <summary>The state file: one "name=version" per line.</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> versions) =>
        string.Concat(versions.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key.Replace("=", "-")}={kv.Value}\n"));

    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = line.IndexOf('=');
            if (i > 0) d[line[..i]] = line[(i + 1)..];
        }
        return d;
    }

    [GeneratedRegex(@"^(?<n>[^_]+)_(?<v>\d+(?:\.\d+){1,3})_")] private static partial Regex PackageFullName();
    [GeneratedRegex(@"^\d+(?:\.\d+){1,3}")] private static partial Regex VersionPrefix();
}
