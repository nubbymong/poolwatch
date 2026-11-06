using System.Text.RegularExpressions;
using PoolWatch.Core.Aicc;
using PoolWatch.Core.Spawn;

namespace PoolWatch.Core.Identity;

public enum OwnerKind { App, Service, ClaudeSession, Aicc, ViaWmi, System, Unknown }

/// <summary>Who a process works for: an app, a service, a Claude Code / AICC session. <see cref="Key"/> groups suspects.</summary>
public sealed record Owner(OwnerKind Kind, string Key, string Name)
{
    /// <summary>The owner is the process itself (an app that is not a shared runtime): "under itself" is never shown.</summary>
    public bool IsSelf { get; init; }
}

/// <summary>
/// What a process is, beyond its image name: "node.exe · Stream Deck plugin com.elgato.cpu", "node.exe · Codex
/// cua_node (server.mjs)", "node.exe · vitest under Claude Code session (sample-app)".
/// </summary>
public sealed record ProcessIdentity(string Image, string? Role, Owner Owner, bool ViaWmi)
{
    /// <summary>Image, role, and the owner when it is not the process itself.</summary>
    public string Display
    {
        get
        {
            string s = Role is null ? Image : $"{Image} · {Role}";
            bool named = Role is not null && Role.Contains(Owner.Name, StringComparison.OrdinalIgnoreCase);
            if (!Owner.IsSelf && !named && Owner.Kind != OwnerKind.Unknown) s += $" under {Owner.Name}";
            if (ViaWmi) s += " (launched via WMI)";
            return s;
        }
    }
}

/// <summary>
/// Builds a <see cref="ProcessIdentity"/> from the exe location (a runtime bundled inside an app's folder vs a system
/// install), the first script / module / subcommand argument, and the nearest named ancestor. Shared runtimes (node,
/// python, pwsh, cmd, conhost, git, bash, ...) are never identified by image name alone.
/// </summary>
public static partial class ProcessIdentities
{
    /// <summary>Processes that run other people's code: an image name alone says nothing about who started them.</summary>
    public static readonly IReadOnlySet<string> SharedRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "node.exe", "node_repl.exe", "python.exe", "pythonw.exe", "py.exe", "pwsh.exe", "powershell.exe", "cmd.exe", "conhost.exe", "OpenConsole.exe",
        "git.exe", "bash.exe", "sh.exe", "wsl.exe", "wscript.exe", "cscript.exe", "java.exe", "javaw.exe", "dotnet.exe", "rundll32.exe", "dllhost.exe",
        "grep.exe", "sed.exe", "head.exe", "tail.exe", "ls.exe", "find.exe", "cat.exe", "wc.exe", "tr.exe", "awk.exe", "xargs.exe", "sort.exe", "uniq.exe",
        "rg.exe", "cut.exe", "date.exe", "uname.exe", "dirname.exe", "basename.exe", "mkdir.exe", "rm.exe", "cp.exe", "mv.exe", "touch.exe", "tee.exe",
        "diff.exe", "which.exe", "env.exe", "printf.exe", "echo.exe", "sleep.exe", "stat.exe", "realpath.exe", "taskkill.exe", "tasklist.exe", "where.exe", "findstr.exe", "timeout.exe", "chcp.com", "npm.exe", "npx.exe", "uv.exe", "bun.exe", "deno.exe",
    };

    /// <summary>Ancestors that say nothing about the requester (shells, consoles, launchers): skipped when looking for the owner.</summary>
    private static readonly HashSet<string> Transparent = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "sihost.exe", "RuntimeBroker.exe", "WindowsTerminal.exe", "wt.exe", "OpenConsole.exe", "conhost.exe", "userinit.exe", "winlogon.exe",
    };

    private static readonly HashSet<string> SystemRoots = new(StringComparer.OrdinalIgnoreCase)
        { "System", "Idle", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "Secure System" };

    /// <summary>Runtime installs whose folder is the runtime itself, not an app that bundles it.</summary>
    private static readonly HashSet<string> RuntimeFolders = new(StringComparer.OrdinalIgnoreCase)
        { "nodejs", "Git", "PowerShell", "WindowsPowerShell", "dotnet", "Java", "Eclipse Adoptium", "nvm", "fnm", "volta", "scoop", "chocolatey", "WinGet", ".nuget", "Programs" };

    /// <param name="chainRootFirst">The process's ancestry, root first, the process last (from the registry).</param>
    /// <param name="useLabel">Include the process's current label (AICC / Electron role); false when computing the label itself.</param>
    public static ProcessIdentity Of(IReadOnlyList<ProcessRecord> chainRootFirst, bool useLabel = true)
    {
        var rec = chainRootFirst[^1];
        string image = rec.Image.Length > 0 ? rec.Image : "?";
        bool viaWmi = chainRootFirst.Take(chainRootFirst.Count - 1).Any(a => a.Image.Equals("WmiPrvSE.exe", StringComparison.OrdinalIgnoreCase));
        var owner = OwnerOf(chainRootFirst);
        string? role = null;
        string? label = useLabel ? rec.Label : null;
        bool shared = SharedRuntimes.Contains(image);
        if (shared)
        {
            // The image path, or the exe the command line starts with when the path could not be read (a process that
            // exited, or access denied): a full path there still says which app bundles the runtime.
            string? path = rec.ImagePath is { Length: > 3 } ip && ip[1] == ':' ? ip : null;
            var bundle = BundleOf(path ?? ExeFromCommandLine(rec.CommandLine));
            string? arg = ArgumentRole(image, rec.CommandLine);
            role = (bundle, arg) switch
            {
                (null, null) => null,
                ({ } b, null) => b,
                (null, { } a) => a,
                ({ } b, { } a) => $"{b} ({a})",
            };
        }
        // Anything else is named by its own image; only its label (AICC / Electron role) or services add to that.
        if (!shared) role = label ?? (rec.Services.Count > 0 ? string.Join(", ", rec.Services) : null);
        return new ProcessIdentity(image, role, owner, viaWmi);
    }

    /// <summary>The first command-line token when it is a full path (C:\...\node.exe), else null.</summary>
    public static string? ExeFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var args = CommandLineTokens.Split(commandLine);
        return args.Count > 0 && args[0].Length > 3 && args[0][1] == ':' && args[0][2] == '\\' ? args[0] : null;
    }

    /// <summary>
    /// A Git Bash / MSYS / Cygwin tool (cut, date, uname, ...): never an owner of its own. Known by its folder, so the
    /// whole coreutils set counts without listing every name.
    /// </summary>
    public static bool IsShellTool(ProcessRecord r) =>
        r.ImagePath is { } p && (p.Contains(@"\Git\usr\bin\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\msys64\usr\bin\", StringComparison.OrdinalIgnoreCase) ||
                                 p.Contains(@"\cygwin64\bin\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\Git\mingw64\bin\", StringComparison.OrdinalIgnoreCase));

    /// <summary>The nearest named ancestor (or the process itself when it is not a shared runtime).</summary>
    public static Owner OwnerOf(IReadOnlyList<ProcessRecord> chainRootFirst)
    {
        for (int i = chainRootFirst.Count - 1; i >= 0; i--)
        {
            var r = chainRootFirst[i];
            if (IsClaudeCode(r)) return new Owner(OwnerKind.ClaudeSession, $"claude:{r.Key}", ClaudeSessionName(r));
            if (r.Image.Equals(ProcessLabels.AiccExe, StringComparison.OrdinalIgnoreCase) && i < chainRootFirst.Count - 1)
                return new Owner(OwnerKind.Aicc, "aicc", "AI Code Conductor");
            if (r.Image.Equals("WmiPrvSE.exe", StringComparison.OrdinalIgnoreCase) && i < chainRootFirst.Count - 1)
            {
                var below = chainRootFirst[i + 1];
                return new Owner(OwnerKind.ViaWmi, $"wmi:{below.Key}", $"a WMI-launched {below.Image}");
            }
            if (SystemRoots.Contains(r.Image)) break;
            if (Transparent.Contains(r.Image) || SharedRuntimes.Contains(r.Image) || IsShellTool(r)) continue;
            bool isSelf = i == chainRootFirst.Count - 1;
            if (r.Services.Count > 0) return new Owner(OwnerKind.Service, ServiceKey + string.Join(",", r.Services), $"{r.Image} ({string.Join(", ", r.Services)})") { IsSelf = isSelf };
            string name = AppName(r);
            return new Owner(OwnerKind.App, "app:" + name.ToLowerInvariant(), name) { IsSelf = isSelf };
        }
        // Only runtimes / shells up to the root: a bundled runtime names its app; else the top-most runtime is the owner.
        var self = chainRootFirst[^1];
        if (BundleOf(self.ImagePath) is { } bundle) return new Owner(OwnerKind.App, "app:" + bundle.ToLowerInvariant(), bundle);
        var top = chainRootFirst.FirstOrDefault(r => !SystemRoots.Contains(r.Image) && !Transparent.Contains(r.Image)) ?? self;
        return new Owner(OwnerKind.Unknown, $"proc:{top.Key}", $"{top.Image} (PID {top.Pid})");
    }

    /// <summary>A service owner's key: this, then the names of the services its process runs.</summary>
    private const string ServiceKey = "svc:";

    /// <summary>The names of the services a service owner stands for (read back from its key); none for any other owner.</summary>
    public static IReadOnlyList<string> ServicesOf(Owner owner) =>
        owner.Kind == OwnerKind.Service && owner.Key.StartsWith(ServiceKey, StringComparison.Ordinal) ? owner.Key[ServiceKey.Length..].Split(',') : [];

    /// <summary>"Codex", "Stream Deck", "AI Code Conductor": the exe name without .exe, with a few friendly names.</summary>
    public static string AppName(ProcessRecord r)
    {
        string n = r.Image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? r.Image[..^4] : r.Image;
        return n.ToLowerInvariant() switch
        {
            "streamdeck" => "Stream Deck",
            "codex" => "Codex",
            "chatgpt" => "ChatGPT",
            "code" => "VS Code",
            "msedge" => "Edge",
            _ => n,
        };
    }

    public static bool IsClaudeCode(ProcessRecord r) =>
        r.Image.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) ||
        (r.Image.Equals("node.exe", StringComparison.OrdinalIgnoreCase) && r.CommandLine is { } cl &&
         (cl.Contains("@anthropic-ai", StringComparison.OrdinalIgnoreCase) || cl.Contains("claude-code", StringComparison.OrdinalIgnoreCase)));

    private static string ClaudeSessionName(ProcessRecord r)
    {
        SessionContext? ctx;
        lock (r.Context) ctx = SessionContext.From(r.Context);
        string? where = ctx?.Cwd is { } cwd ? Leaf(cwd) : null;
        return where is null ? $"Claude Code session (PID {r.Pid})" : $"Claude Code session {where} (PID {r.Pid})";
    }

    /// <summary>The app that bundles a runtime, from its exe location; null for a system install or an unknown path.</summary>
    public static string? BundleOf(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath)) return null;
        string p = WinPath.Normalize(imagePath);
        if (StreamDeck().Match(p) is { Success: true }) return "Stream Deck";
        if (RuntimesDir().Match(p) is { Success: true } rt)
        {
            string app = rt.Groups["app"].Value;
            return $"{(app.Equals("Codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : app)} {rt.Groups["rt"].Value}";
        }
        // <root>\<vendor>[\<product>]\...\x.exe under AppData or Program Files.
        var m = AppFolder().Match(p);
        if (!m.Success) return null;
        string vendor = m.Groups["v"].Value, product = m.Groups["p"].Success ? m.Groups["p"].Value : "";
        // Store apps: Program Files\WindowsApps\<Publisher.App>_<version>_<arch>__<id>\...; the per-user alias folder is not an app.
        if (vendor.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
            return product.Length > 0 && product.Split('_')[0] is { Length: > 0 } pkg ? pkg : null;
        if (vendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && product.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return null;
        if (vendor.Equals("Programs", StringComparison.OrdinalIgnoreCase) && product.Length > 0) { vendor = product; product = ""; }
        if (RuntimeFolders.Contains(vendor) || RuntimeFolders.Contains(product) && vendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) return null;
        if (vendor.StartsWith("Python", StringComparison.OrdinalIgnoreCase) || vendor.StartsWith("node-v", StringComparison.OrdinalIgnoreCase) ||
            vendor.StartsWith("jdk", StringComparison.OrdinalIgnoreCase) || vendor.StartsWith("jre", StringComparison.OrdinalIgnoreCase)) return null;
        if (vendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && product.Length > 0) return product;
        return product.Length > 0 && !product.Contains('.') && !product.Equals("bin", StringComparison.OrdinalIgnoreCase) && !product.Equals(vendor, StringComparison.OrdinalIgnoreCase)
            && !RuntimeFolders.Contains(product) && !product.StartsWith("jdk", StringComparison.OrdinalIgnoreCase) ? $"{vendor} {product}" : vendor;
    }

    [GeneratedRegex(@"\\Elgato\\StreamDeck\\", RegexOptions.IgnoreCase)] private static partial Regex StreamDeck();
    [GeneratedRegex(@"\\(?<app>[^\\]+)\\runtimes\\(?<rt>[^\\]+)\\", RegexOptions.IgnoreCase)] private static partial Regex RuntimesDir();
    [GeneratedRegex(@"^[A-Z]:\\(?:Users\\[^\\]+\\AppData\\(?:Local|Roaming|LocalLow)|Program Files(?: \(x86\))?|ProgramData)\\(?<v>[^\\]+)(?:\\(?<p>[^\\]+))?\\", RegexOptions.IgnoreCase)]
    private static partial Regex AppFolder();
    [GeneratedRegex(@"[\\/]Plugins[\\/](?<p>[^\\/]+?)\.sdPlugin[\\/]", RegexOptions.IgnoreCase)] private static partial Regex SdPlugin();
    [GeneratedRegex(@"node_modules[\\/](?<p>@[^\\/]+[\\/][^\\/]+|[^\\/@.][^\\/]*)", RegexOptions.IgnoreCase)] private static partial Regex NodeModule();

    /// <summary>The first script, module or subcommand argument, as a short label.</summary>
    public static string? ArgumentRole(string image, string? commandLine)
    {
        if (image.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) || image.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase)) return "console host";
        if (string.IsNullOrEmpty(commandLine)) return null;
        if (SdPlugin().Match(commandLine) is { Success: true } sd) return $"Stream Deck plugin {sd.Groups["p"].Value}";
        var args = CommandLineTokens.Split(commandLine);
        string img = image.ToLowerInvariant();
        switch (img)
        {
            case "node.exe":
            case "node_repl.exe":
            case "bun.exe":
            case "deno.exe":
                for (int i = 1; i < args.Count; i++)
                {
                    string a = args[i];
                    if (a is "-r" or "--require" or "--import" or "--loader" or "-e" or "--eval" or "-p") { if (a is "-e" or "--eval" or "-p") return "inline script"; i++; continue; }
                    if (a.StartsWith('-')) continue;
                    return ScriptName(a);
                }
                return img == "node_repl.exe" ? "REPL" : null;
            case "python.exe":
            case "pythonw.exe":
            case "py.exe":
                for (int i = 1; i < args.Count; i++)
                {
                    string a = args[i];
                    if (a == "-m" && i + 1 < args.Count) return args[i + 1];
                    if (a == "-c") return "inline script";
                    if (a is "-W" or "-X" or "--check-hash-based-pycs") { i++; continue; }
                    if (a.StartsWith('-')) continue;
                    return ScriptName(a);
                }
                return null;
            case "pwsh.exe":
            case "powershell.exe":
                for (int i = 1; i < args.Count; i++)
                {
                    string a = args[i].ToLowerInvariant();
                    if (a is "-file" or "-f" && i + 1 < args.Count) return Leaf(args[i + 1]);
                    if (a is "-command" or "-c" && i + 1 < args.Count) return "command: " + Short(string.Join(' ', args.Skip(i + 1)));
                    if (a is "-encodedcommand" or "-enc" or "-e" or "-ec") return "encoded command";
                    if (a is "-noprofile" or "-nologo" or "-noninteractive" or "-nop" or "-noexit") continue;
                    if (a is "-executionpolicy" or "-ep" or "-windowstyle" or "-w") { i++; continue; }
                    if (!a.StartsWith('-')) return Leaf(args[i]);
                }
                return null;
            case "cmd.exe":
                for (int i = 1; i < args.Count; i++)
                    if (args[i].Equals("/c", StringComparison.OrdinalIgnoreCase) || args[i].Equals("/k", StringComparison.OrdinalIgnoreCase))
                        return i + 1 < args.Count ? "/c " + Short(Leaf(args[i + 1].Trim('"'))) : null;
                return null;
            case "bash.exe":
            case "sh.exe":
                for (int i = 1; i < args.Count; i++)
                {
                    if (args[i] is "-c" or "-lc" or "-ic")
                    {
                        // bash -c -l "cmd": options may follow -c before the command string.
                        int j = i + 1;
                        while (j < args.Count && args[j].StartsWith('-') && args[j].Length > 1) j++;
                        return j < args.Count ? "-c " + Short(args[j].TrimStart().Split(' ', 2)[0]) : null;
                    }
                    if (args[i].StartsWith('-')) continue;
                    return Leaf(args[i]);
                }
                return null;
            case "git.exe":
                for (int i = 1; i < args.Count; i++)
                {
                    if (args[i] is "-C" or "-c" or "--git-dir" or "--work-tree" && i + 1 < args.Count) { i++; continue; }
                    if (args[i].StartsWith('-')) continue;
                    return args[i];
                }
                return null;
            case "conhost.exe":
            case "openconsole.exe":
                return "console host";
            default:
                return null;
        }
    }

    /// <summary>A package name for a script inside node_modules, else the file name.</summary>
    private static string ScriptName(string path)
    {
        if (NodeModule().Matches(path) is { Count: > 0 } ms) return ms[^1].Groups["p"].Value.Replace('\\', '/');
        return Leaf(path);
    }

    private static string Leaf(string path)
    {
        path = path.TrimEnd('\\', '/');
        int i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
