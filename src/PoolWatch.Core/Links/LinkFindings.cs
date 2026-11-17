namespace PoolWatch.Core.Links;

/// <summary>
/// What the AppData link finder found: a PATH entry reached through a junction or symbolic link located under AppData
/// (with the folder it leads to, when PoolWatch may put that in its place), one whose link leads to a folder that is not
/// there (on a drive that is), winget's Links
/// folder on PATH, uv's Pythons, pnpm's global packages and the Codex command-line tool kept under AppData, Claude Code's
/// temp folder as a junction, an app's own data folder as a junction, a venv on PATH whose Python is reached through such
/// a link, Claude Code hooks that start Python, and the advice about projects, which PoolWatch does not look inside.
/// </summary>
public enum LinkKind { PathEntry, PathEntryGone, WinGetLinks, Uv, Pnpm, Codex, ClaudeTemp, AppFolder, Venv, Hook, Projects }

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
    /// <summary>
    /// A PATH entry PoolWatch may replace: the folder the link leads to, written in its place. A PATH entry whose link leads
    /// nowhere: the folder it leads to, which is not there. Claude Code's temp folder or an app's data folder: where its
    /// link leads, when that could be read.
    /// </summary>
    public string? Target { get; init; }
    /// <summary>A PATH entry PoolWatch does not replace: why, in plain words.</summary>
    public string? WhyNot { get; init; }
    /// <summary>
    /// A PATH entry PoolWatch may replace: the folder its link leads to is on that PATH already, as another entry, so the fix
    /// removes this entry rather than write the folder a second time (codex update puts its link back after each fix).
    /// </summary>
    public bool OnPathAlready { get; init; }
    /// <summary>uv's minor-version links, winget's links, the hooks that start Python: how many were found.</summary>
    public int Count { get; init; }
    /// <summary>uv's minor-version links: their folder names; the hooks: their commands (at most <see cref="LinkFinder.MaxNames"/>).</summary>
    public IReadOnlyList<string> Names { get; init; } = [];
    /// <summary>The guided fix: the variables it sets for the account. Empty when there is nothing left to set.</summary>
    public IReadOnlyList<EnvSetting> EnvSettings { get; init; } = [];
    /// <summary>The guided fix: its variables already point outside AppData (the links found are left from before).</summary>
    public bool Moved { get; init; }
    /// <summary>
    /// Null: in the account's own AppData. "Claude" or "Codex": in that app's own copy of AppData
    /// (%LOCALAPPDATA%\Packages\...\LocalCache), where a program the app started made it; programs outside the app do not
    /// see it, and PoolWatch changes nothing there.
    /// </summary>
    public string? InApp { get; init; }
    /// <summary>The size in bytes of the block each open through the link keeps, from its reparse data (<see cref="LinkBlocks.Expected"/>), when it was read.</summary>
    public int? Block { get; init; }
    /// <summary>A data folder: whose app it is ("Claude", "Codex"); a venv: the tool that made it, when its folder shows it ("PlatformIO").</summary>
    public string? Tool { get; init; }
    /// <summary>A venv: the folder of the Python it runs (its home in pyvenv.cfg), as the window shows it.</summary>
    public string? Home { get; init; }

    /// <summary>What tells two rows apart, and the same row in a later look.</summary>
    public string Key => $"{Kind}:{EnvScopes.Word(Scope)}:{Index}:{Entry}:{Link}:{InApp}";

    /// <summary>
    /// The window's button changes something for this row: replaces an entry of the account's own PATH (or removes it, when
    /// its folder is on that PATH already), removes one whose link leads nowhere, or sets the variables. Never for an entry
    /// of the machine's PATH, which every account and Windows' own services read: that one is the user's to change. Never
    /// for a row in an app's own copy of AppData.
    /// </summary>
    public bool CanFix => InApp is null && Kind switch
    {
        LinkKind.PathEntry => Scope == EnvScope.User && Target is not null,
        LinkKind.PathEntryGone => Scope == EnvScope.User,
        LinkKind.Uv or LinkKind.Pnpm or LinkKind.Codex or LinkKind.ClaudeTemp => EnvSettings.Count > 0,
        _ => false,
    };

    /// <summary>The window's button shows the steps to follow, with a prompt for the user's AI agent, and changes nothing.</summary>
    public bool HasSteps => InApp is null && Kind is LinkKind.AppFolder or LinkKind.Venv or LinkKind.Hook;

    /// <summary>The window's button does something for this row.</summary>
    public bool Actionable => CanFix || HasSteps;
}

/// <summary>A look at this PC for the links that leak: whether the Claude or Codex desktop app is installed, and what was found.</summary>
public sealed record LinkScan(DateTime ScannedUtc, bool ClaudeInstalled, bool CodexInstalled, IReadOnlyList<LinkFinding> Findings)
{
    /// <summary>One of the two apps whose package context has the leak is installed for this account: only then is anything looked for.</summary>
    public bool Applies => ClaudeInstalled || CodexInstalled;

    /// <summary>The rows the window's button changes something for.</summary>
    public int ToFix => Findings.Count(f => f.CanFix);

    /// <summary>The rows the window's button does something for: a change, or steps to follow (what the Simple view's suggestion counts).</summary>
    public int ToAct => Findings.Count(f => f.Actionable);

    public static LinkScan None(DateTime nowUtc) => new(nowUtc, false, false, []);
}

/// <summary>A program of the Claude or Codex desktop app running now: its file name, which app, and when it started.</summary>
public sealed record AppProcess(string Program, string App, DateTime StartedUtc);

/// <summary>The bounds and fixed names the finder and its fixes share.</summary>
public static class LinkFinder
{
    /// <summary>The most uv link names or hook commands a row keeps, and the most folders read in one folder.</summary>
    public const int MaxNames = 8, MaxFolders = 256;

    /// <summary>The variables the guided fix may set: no record or click makes PoolWatch set any other.</summary>
    public const string UvPythons = "UV_PYTHON_INSTALL_DIR", UvCache = "UV_CACHE_DIR", PnpmHome = "PNPM_HOME", CodexInstall = "CODEX_INSTALL_DIR",
        ClaudeTemp = "CLAUDE_CODE_TMPDIR";

    public static readonly IReadOnlyList<string> Variables = [UvPythons, UvCache, PnpmHome, CodexInstall, ClaudeTemp];

    /// <summary>Where each variable is set to: a folder under the account's profile, outside AppData.</summary>
    public static string FolderFor(string variable, string profile) => profile.TrimEnd('\\') + "\\" + (variable switch
    {
        UvPythons => @".uv\python",
        UvCache => @".uv\cache",
        PnpmHome => ".pnpm",
        CodexInstall => @".codex\bin",
        ClaudeTemp => ".claude-tmp",
        _ => throw new ArgumentException("not a variable PoolWatch sets", nameof(variable)),
    });

    /// <summary>The issue the finder is about: its id in the knowledge packs.</summary>
    public const string KnownIssueId = "ntfc-appdata-links";

    /// <summary>Where the measurements are, for the window's details and the AI agent's prompt.</summary>
    public const string IssueUrl = "https://github.com/anthropics/claude-code/issues/96870";

    /// <summary>Where the retry loop under Claude Code's temp folder was measured.</summary>
    public const string TempLoopUrl = "https://github.com/anthropics/claude-code/issues/95489";
}
