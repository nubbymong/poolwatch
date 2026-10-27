using System.Text.RegularExpressions;
using PoolWatch.Core.Model;

namespace PoolWatch.Core.Aicc;

/// <summary>
/// Electron / Chromium child roles and AI Code Conductor (AICC) roles (DESIGN.md section 9). Pure functions over the
/// registry's records so they can be tested on fixtures.
/// </summary>
public static partial class ProcessLabels
{
    public const string AiccExe = "AI Code Conductor.exe";
    public static readonly HashSet<string> PtyHosts = new(StringComparer.OrdinalIgnoreCase) { "conhost.exe", "OpenConsole.exe" };
    public static readonly HashSet<string> SessionClis = new(StringComparer.OrdinalIgnoreCase)
        { "claude.exe", "codex.exe", "node.exe", "bash.exe", "pwsh.exe", "powershell.exe", "ssh.exe", "cmd.exe", "sh.exe", "git.exe" };

    [GeneratedRegex(@"--type=(?<t>[\w-]+)")] private static partial Regex TypeArg();
    [GeneratedRegex(@"--utility-sub-type=(?<t>[\w.\-]+)")] private static partial Regex SubTypeArg();

    /// <summary>"renderer", "gpu-process", "utility (node.mojom.NodeService)", or null for a non-Chromium process.</summary>
    public static string? ElectronRole(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var t = TypeArg().Match(commandLine);
        if (!t.Success) return null;
        string role = t.Groups["t"].Value;
        var sub = SubTypeArg().Match(commandLine);
        return sub.Success ? $"{role} ({sub.Groups["t"].Value})" : role;
    }

    public static bool IsNodeService(string? commandLine) => commandLine?.Contains("--utility-sub-type=node.mojom.NodeService", StringComparison.Ordinal) == true;

    /// <summary>
    /// Sets <see cref="ProcessRecord.Label"/> for Electron children and the AICC tree. <paramref name="readOps"/> is each
    /// process's read-operation count (latest snapshot); the AICC NodeService worker with by far the most reads (at
    /// least twice the next) is the Tokenomics worker, which sweeps Claude Code transcripts.
    /// </summary>
    public static void Apply(ProcessRegistry registry, IReadOnlyDictionary<ProcessKey, long> readOps)
    {
        var live = registry.LiveRecords();
        var workers = new List<ProcessRecord>();
        foreach (var rec in live)
        {
            var chain = registry.Ancestry(rec.Key);
            bool underAicc = chain.Take(chain.Count - 1).Any(a => a.Image.Equals(AiccExe, StringComparison.OrdinalIgnoreCase));
            bool isAicc = rec.Image.Equals(AiccExe, StringComparison.OrdinalIgnoreCase);
            string? role = ElectronRole(rec.CommandLine);
            if (isAicc)
            {
                if (role is null) rec.Label = "AICC main";
                else if (IsNodeService(rec.CommandLine)) { rec.Label = "AICC worker"; workers.Add(rec); }
                else rec.Label = "AICC " + role;
            }
            else if (underAicc && PtyHosts.Contains(rec.Image)) rec.Label = "AICC PTY host";
            else if (underAicc && SessionClis.Contains(rec.Image)) rec.Label = "AICC session";
            else if (Identity.ProcessIdentities.SharedRuntimes.Contains(rec.Image) || chain.Any(a => a.Image.Equals("WmiPrvSE.exe", StringComparison.OrdinalIgnoreCase)))
            {
                // node.exe · Stream Deck plugin x under Stream Deck: what it runs and for whom (labels are recomputed each minute).
                var id = Identity.ProcessIdentities.Of(chain, useLabel: false);
                string text = id.Display.StartsWith(rec.Image + " · ", StringComparison.OrdinalIgnoreCase) ? id.Display[(rec.Image.Length + 3)..]
                    : id.Display.StartsWith(rec.Image, StringComparison.OrdinalIgnoreCase) ? id.Display[rec.Image.Length..].Trim() : id.Display;
                rec.Label = text.Length > 0 ? text : null;
            }
            else if (role is not null)
            {
                // Any other Electron / Chromium app: name the role after the app's root process.
                var root = chain.FirstOrDefault(a => a.Image.Equals(rec.Image, StringComparison.OrdinalIgnoreCase) && ElectronRole(a.CommandLine) is null);
                rec.Label = $"{Path.GetFileNameWithoutExtension(root?.Image ?? rec.Image)} {role}";
            }
        }
        if (workers.Count >= 1)
        {
            var ranked = workers.OrderByDescending(w => readOps.GetValueOrDefault(w.Key)).ToList();
            long top = readOps.GetValueOrDefault(ranked[0].Key), next = ranked.Count > 1 ? readOps.GetValueOrDefault(ranked[1].Key) : 0;
            if (top > 0 && top >= 2 * next) ranked[0].Label = "AICC Tokenomics worker";
        }
    }
}

/// <summary>Who a session CLI belongs to: working directory and account profile (from the process environment).</summary>
public sealed record SessionContext(string? Cwd, string? Profile, string? ConfigDir)
{
    public const string CwdKey = "cwd", ConfigDirKey = "env.CLAUDE_CONFIG_DIR", UserProfileKey = "env.USERPROFILE";

    private static readonly Regex ProfileRx = new(@"account-profiles[\\/](?<p>profile-[^\\/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>From the values the PEB reader stored in <see cref="ProcessRecord.Context"/>.</summary>
    public static SessionContext? From(IReadOnlyDictionary<string, string> context)
    {
        context.TryGetValue(CwdKey, out var cwd);
        context.TryGetValue(ConfigDirKey, out var cfg);
        context.TryGetValue(UserProfileKey, out var home);
        if (cwd is null && cfg is null && home is null) return null;
        string? profile = null;
        foreach (var p in new[] { cfg, home })
            if (p is not null && ProfileRx.Match(p) is { Success: true } m) { profile = m.Groups["p"].Value; break; }
        string? configDir = cfg ?? (home is not null ? Path.Combine(home, ".claude") : null);
        return new SessionContext(cwd, profile, configDir);
    }

    /// <summary>Merged along the chain (suspect first, then ancestors): the nearest process that knows each value wins.</summary>
    public static SessionContext? ForChain(IReadOnlyList<ProcessRecord> chainRootFirst)
    {
        var merged = new Dictionary<string, string>();
        for (int i = chainRootFirst.Count - 1; i >= 0; i--)
        {
            lock (chainRootFirst[i].Context) // filled from the ETW thread
                foreach (var (k, v) in chainRootFirst[i].Context) merged.TryAdd(k, v);
        }
        return From(merged);
    }

    private static readonly Regex AiccRootRx = new(@"^(?<root>[A-Za-z]:[\\/].*?)[\\/]account-profiles[\\/]profile-[^\\/]+(?:[\\/]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The AI Code Conductor resources folders whose <c>account-profiles\profile-*</c> folders are searched for transcripts:
    /// the AiccResourcesRoot setting when it is set, and the folder the session's own config folder lives in
    /// (<c>&lt;root&gt;\account-profiles\profile-&lt;id&gt;\.claude</c>), so the default (empty) setting finds an AICC install
    /// by itself. Local drive paths only (never a share); at most two.
    /// </summary>
    public static IReadOnlyList<string> AiccRoots(string? setting, string? sessionConfigDir)
    {
        var roots = new List<string>();
        void Add(string candidate)
        {
            if (!Settings.IsLocalDrivePath(candidate)) return;
            string t = candidate.TrimEnd('\\', '/');
            string root = t.Length == 2 ? t + "\\" : t; // "D:\" stays a drive root, never the drive-relative "D:"
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
        }
        Add(setting?.Trim() ?? "");
        if (sessionConfigDir is not null && AiccRootRx.Match(sessionConfigDir.Trim()) is { Success: true } m) Add(m.Groups["root"].Value);
        return roots;
    }

    /// <summary>"AICC › D:\sample-app (profile-xxxx) › bash › find.exe".</summary>
    public static string Describe(IReadOnlyList<ProcessRecord> chainRootFirst, SessionContext? session)
    {
        int aicc = -1;
        for (int i = 0; i < chainRootFirst.Count; i++)
            if (chainRootFirst[i].Image.Equals(ProcessLabels.AiccExe, StringComparison.OrdinalIgnoreCase)) aicc = i;
        if (aicc < 0) return string.Join(" › ", chainRootFirst.Select(r => r.Image));
        var parts = new List<string> { "AICC" };
        if (session?.Cwd is { } cwd) parts.Add(session.Profile is { } p ? $"{cwd.TrimEnd('\\')} ({p})" : cwd.TrimEnd('\\'));
        else if (session?.Profile is { } p2) parts.Add($"({p2})");
        // Skip PTY hosts and the AICC process itself; keep the session CLIs down to the suspect.
        // Session CLIs without ".exe" (as in "bash › find.exe"); the suspect itself keeps its image name.
        var rest = chainRootFirst.Skip(aicc + 1).Where(r => !ProcessLabels.PtyHosts.Contains(r.Image)).ToList();
        parts.AddRange(rest.Select((r, i) => i == rest.Count - 1 ? r.Image : Path.GetFileNameWithoutExtension(r.Image)));
        return string.Join(" › ", parts);
    }
}

/// <summary>Parses a Windows environment block ("NAME=value\0...\0\0").</summary>
public static class EnvironmentBlock
{
    public static Dictionary<string, string> Parse(string block, IReadOnlyCollection<string> wanted)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block.Split('\0'))
        {
            if (entry.Length < 2) continue;  // the block ends with \0\0: empty entries
            int eq = entry.IndexOf('=', 1); // per-drive cwd entries like "=C:=C:\x" start with '='
            if (eq <= 0) continue;
            var name = entry[..eq];
            if (wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) env[name] = entry[(eq + 1)..];
        }
        return env;
    }
}
