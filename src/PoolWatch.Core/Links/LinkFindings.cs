namespace PoolWatch.Core.Links;

/// <summary>
/// What the AppData link finder found: a PATH entry reached through a junction or symbolic link located under AppData
/// (with the folder it leads to, when PoolWatch may put that in its place), one whose link leads nowhere, winget's Links
/// folder on PATH, uv's Pythons, pnpm's global packages and the Codex command-line tool kept under AppData, and the
/// advice about projects, which PoolWatch does not look inside.
/// </summary>
public enum LinkKind { PathEntry, PathEntryGone, WinGetLinks, Uv, Pnpm, Codex, Projects }

/// <summary>One environment variable the guided fix sets for the account: its name, the folder it is set to, and its value as shown (null: not set).</summary>
public sealed record EnvSetting(string Name, string Value, string? Seen);

/// <summary>One thing the finder found, as the AppData links window shows it in a row.</summary>
public sealed record LinkFinding
{
    public required LinkKind Kind { get; init; }
    /// <summary>A PATH entry: whose PATH it is on.</summary>
    public EnvScope Scope { get; init; }
    /// <summary>A PATH entry: its place in the PATH value, from 0.</summary>
    public int Index { get; init; } = -1;
    /// <summary>A PATH entry: the entry exactly as written in the PATH value.</summary>
    public string Entry { get; init; } = "";
    /// <summary>The link found (a full path, %NAME% filled in), or the folder the row is about.</summary>
    public string Link { get; init; } = "";
    /// <summary>A PATH entry PoolWatch may replace: the folder the link leads to, written in its place.</summary>
    public string? Target { get; init; }
    /// <summary>A PATH entry PoolWatch does not replace: why, in plain words.</summary>
    public string? WhyNot { get; init; }
    /// <summary>uv's minor-version links, winget's links: how many were found.</summary>
    public int Count { get; init; }
    /// <summary>uv's minor-version links: their folder names (at most <see cref="LinkFinder.MaxNames"/>).</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
    /// <summary>The guided fix: the variables it sets for the account. Empty when there is nothing left to set.</summary>
    public IReadOnlyList<EnvSetting> EnvSettings { get; init; } = [];
    /// <summary>The guided fix: its variables already point outside AppData (the links found are left from before).</summary>
    public bool Moved { get; init; }

    /// <summary>What tells two rows apart, and the same row in a later look.</summary>
    public string Key => $"{Kind}:{EnvScopes.Word(Scope)}:{Index}:{Entry}:{Link}";

    /// <summary>The window's button does something for this row: replaces the entry, or sets the variables.</summary>
    public bool CanFix => Kind switch
    {
        LinkKind.PathEntry => Target is not null,
        LinkKind.Uv or LinkKind.Pnpm or LinkKind.Codex => EnvSettings.Count > 0,
        _ => false,
    };
}

/// <summary>A look at this PC for the links that leak: whether the Claude or Codex desktop app is installed, and what was found.</summary>
public sealed record LinkScan(DateTime ScannedUtc, bool ClaudeInstalled, bool CodexInstalled, IReadOnlyList<LinkFinding> Findings)
{
    /// <summary>One of the two apps whose package context has the leak is installed for this account: only then is anything looked for.</summary>
    public bool Applies => ClaudeInstalled || CodexInstalled;

    /// <summary>The rows the window's button fixes.</summary>
    public int ToFix => Findings.Count(f => f.CanFix);

    public static LinkScan None(DateTime nowUtc) => new(nowUtc, false, false, []);
}

/// <summary>The bounds and fixed names the finder and its fixes share.</summary>
public static class LinkFinder
{
    /// <summary>The most uv link names a row keeps, and the most folders read in one folder.</summary>
    public const int MaxNames = 8, MaxFolders = 256;

    /// <summary>The variables the guided fix may set: no record or click makes PoolWatch set any other.</summary>
    public const string UvPythons = "UV_PYTHON_INSTALL_DIR", UvCache = "UV_CACHE_DIR", PnpmHome = "PNPM_HOME", CodexInstall = "CODEX_INSTALL_DIR";

    public static readonly IReadOnlyList<string> Variables = [UvPythons, UvCache, PnpmHome, CodexInstall];

    /// <summary>Where each variable is set to: a folder under the account's profile, outside AppData.</summary>
    public static string FolderFor(string variable, string profile) => profile.TrimEnd('\\') + "\\" + (variable switch
    {
        UvPythons => @".uv\python",
        UvCache => @".uv\cache",
        PnpmHome => ".pnpm",
        CodexInstall => @".codex\bin",
        _ => throw new ArgumentException("not a variable PoolWatch sets", nameof(variable)),
    });

    /// <summary>The issue the finder is about: its id in the knowledge packs.</summary>
    public const string KnownIssueId = "ntfc-appdata-links";

    /// <summary>Where the measurements are, for the window's details and the AI agent's prompt.</summary>
    public const string IssueUrl = "https://github.com/anthropics/claude-code/issues/96870";
}
