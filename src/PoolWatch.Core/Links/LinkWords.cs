using System.Text;

namespace PoolWatch.Core.Links;

/// <summary>
/// The AppData links window in plain words: each row's three columns, its details (what it is, what it costs, what fixes
/// it, who measured it), the question before a change, the steps that are left to the user after the guided fix, and the
/// prompt for the user's AI agent. The costs and fixes are the measurements in anthropics/claude-code issue 96870, each
/// with the GitHub name of whoever made it. Pure, so the Linux tests read every word.
/// </summary>
public static class LinkWords
{
    public const string WindowTitle = "Links under AppData";
    public const string Lead = "Inside the Claude and Codex desktop apps, every open through a junction or link under AppData keeps a small " +
                               "block of the kernel pool until Windows restarts. These are the links PoolWatch found that the apps' tools go through.";
    public const string NothingFound = "PoolWatch found no link under AppData on your PATH or in uv, pnpm or the Codex command-line tool.";
    public const string NotInstalled = "Neither the Claude nor the Codex desktop app is installed for your account, so PoolWatch did not look: " +
                                       "programs outside those apps keep nothing.";
    public const string Looking = "Looking for links under AppData...";
    public const string Restart = "Quit the Claude and Codex apps fully and start them again: they see a new PATH or variable only then.";

    public const string WhatColumn = "What", WhereColumn = "Where", FixColumn = "What fixes it";

    /// <summary>The button under the list for the selected row: what it does, or empty (greyed) when it does nothing.</summary>
    public static string Button(LinkFinding f) => !f.CanFix ? "Fix" : f.Kind == LinkKind.PathEntry ? "Replace on PATH" : "Set the variables...";

    public const string LookAgain = "Look again", Close = "Close";

    /// <summary>The row's first column.</summary>
    public static string What(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry or LinkKind.PathEntryGone => f.Entry.Trim(),
        LinkKind.WinGetLinks => $"winget's Links folder ({Count(f.Count, "link")})",
        LinkKind.Uv => f.Moved ? "uv's old Python links" : $"uv's Pythons ({Count(f.Count, "link")})",
        LinkKind.Pnpm => "pnpm's global packages",
        LinkKind.Codex => "The Codex command-line tool",
        _ => "Links in your projects",
    };

    /// <summary>The row's second column.</summary>
    public static string Where(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry or LinkKind.PathEntryGone or LinkKind.WinGetLinks => f.Scope == EnvScope.User ? "your PATH" : "the machine's PATH",
        LinkKind.Projects => "your projects",
        _ => f.Link,
    };

    /// <summary>The row's third column.</summary>
    public static string Fix(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry => f.Target is not null ? "Replace it with the folder it leads to" : "Change it yourself",
        LinkKind.PathEntryGone => "Remove it yourself",
        LinkKind.Uv => f.EnvSettings.Count > 0 ? "Move uv out of AppData" : "Rebuild what still uses them",
        LinkKind.Pnpm => f.EnvSettings.Count > 0 ? "Move pnpm out of AppData" : "Reinstall its global packages",
        LinkKind.Codex => f.EnvSettings.Count > 0 ? "Install it outside AppData" : "Run its update again",
        _ => "Advice only",
    };

    /// <summary>The details under the list: what it is, what it costs, what fixes it and what PoolWatch does.</summary>
    public static string Details(LinkFinding f)
    {
        var sb = new StringBuilder();
        sb.Append(What(f)).Append(" (").Append(Where(f)).Append(')').Append("\r\n\r\n");
        sb.Append("What it is: ").Append(Is(f)).Append("\r\n\r\n");
        sb.Append("What it costs: ").Append(Costs(f)).Append("\r\n\r\n");
        sb.Append("What fixes it: ").Append(Fixes(f)).Append("\r\n\r\n");
        sb.Append("Read more: ").Append(LinkFinder.IssueUrl);
        return sb.ToString();
    }

    private static string Is(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry or LinkKind.PathEntryGone => $"this entry on {Where(f)} is reached through {f.Link}, a junction or link under AppData.",
        LinkKind.WinGetLinks => "winget puts a link in this folder for each portable app it installs, and the folder is on your PATH.",
        LinkKind.Uv => (f.Moved ? "uv installs its Pythons outside AppData now, but these links under AppData are left from before: " : "") +
                       $"uv reaches its Pythons through a link for each minor version in {f.Link} ({string.Join(", ", f.Names)}" +
                       (f.Count > f.Names.Count ? $" and {f.Count - f.Names.Count} more" : "") + "). Venvs whose home is one of them, and uv's tool launchers, go through them too.",
        LinkKind.Pnpm => f.Moved ? "pnpm's home is outside AppData now, but its global packages are still in the old folder." :
            $"pnpm keeps its global packages in {f.Link}, as a farm of links.",
        LinkKind.Codex => f.Moved ? "the Codex installer now makes its link outside AppData, but the old one may still be on your PATH." :
            $"the Codex command-line tool's installer puts {f.Link} on your PATH as a link under AppData.",
        _ => "a node_modules that is a junction, a venv whose home in pyvenv.cfg is one of uv's links, or a .python-version without an exact patch " +
             "(3.12 rather than 3.12.13) go through a link under AppData the same way. PoolWatch does not look inside your projects.",
    };

    private static string Costs(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry => "every program a terminal or tool inside the Claude or Codex app starts, and every command it looks up, opens the folders on " +
                              "PATH. Through a link under AppData each open keeps a block: about one for every program Git Bash starts, wherever the entry " +
                              "is on PATH (DraftingDreamer), and two for each command lookup that reaches it (peterlau03).",
        LinkKind.PathEntryGone => "the folder the link leads to is gone, as an uninstall can leave it, and every command lookup that reaches this entry " +
                                  "still keeps blocks: 120 for 30 runs of a command that was not found (thyhellcat).",
        LinkKind.WinGetLinks => "running a tool through one of these links keeps a block for each open (thyhellcat). A lookup that finds nothing here keeps nothing.",
        LinkKind.Uv => "each Python start through these links keeps blocks: 64 for python -c pass and about 650 with a few imports (0uternet), about " +
                       "600 for each hook that runs Python (peterlau03), and a pytest run kept about 1,160 a second (0uternet).",
        LinkKind.Pnpm => "about 25 blocks for each run of a global tool (peterlau03).",
        LinkKind.Codex => "its bin folder on PATH costs as any PATH entry through a link does: about one block for every program Git Bash starts " +
                          "(DraftingDreamer). Each codex update puts the link back on PATH (peterlau03).",
        _ => "a test run in a worktree under TEMP whose node_modules was a junction grew the kernel pool by about 15 GB in an hour (bronze1man); " +
             "a venv on uv's link kept about 322 blocks for each python -c pass (morenogironadr).",
    };

    private static string Fixes(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry when f.Target is { } target =>
            $"put the folder the link leads to, {target}, in the entry's place: replacing such entries with their targets measured 0 (peterlau03, " +
            "thyhellcat, DraftingDreamer). PoolWatch changes only this entry and keeps every other one, and the value's kind, as they are. " +
            "If the tool later changes where its link leads (to switch versions, say), your PATH keeps the folder it leads to now: " +
            "Put back in Active changes puts the entry back. " + Restart,
        LinkKind.PathEntry => $"PoolWatch does not replace this entry: {f.WhyNot} You can change it yourself in Windows' Environment Variables window, " +
                              "to a folder that is not reached through a link under AppData.",
        LinkKind.PathEntryGone => "if the tool is gone, remove the entry from PATH in Windows' Environment Variables window. PoolWatch leaves it as it is: " +
                                  "it replaces an entry only with a folder that is there.",
        LinkKind.WinGetLinks => "no change that measured 0 is known for this folder. A tool run from a terminal outside the Claude and Codex apps, or " +
                                "from the folder winget installed it in, keeps nothing.",
        LinkKind.Uv when f.EnvSettings.Count > 0 => "set " + Names(f) + " to folders outside AppData, then reinstall your Pythons and rebuild your " +
            "venvs and uv tools: Python starts then kept 0 (peterlau03, 0uternet, estoll37). " + Guided,
        LinkKind.Uv => "rebuild the venvs and uv tools made before (uv venv -p with an exact version), then remove the old links (rmdir removes the " +
                       "link only): 0uternet did this.",
        LinkKind.Pnpm when f.EnvSettings.Count > 0 => "set PNPM_HOME to a folder outside AppData, then reinstall your global packages: that measured 0 " +
            @"(peterlau03). pnpm's own settings stay in %LOCALAPPDATA%\pnpm\config. " + Guided,
        LinkKind.Pnpm => "reinstall your global packages so they are in pnpm's new home (peterlau03).",
        LinkKind.Codex when f.EnvSettings.Count > 0 => "set CODEX_INSTALL_DIR to a folder outside AppData, so the installer makes its link there " +
            "and codex update stops putting the one under AppData back (peterlau03). " + Guided,
        LinkKind.Codex => "run codex update again from a terminal outside the apps, so it makes its link in the new folder, then replace or remove " +
                          "the old entry on your PATH.",
        _ => "pin an exact Python version (3.12.13, not 3.12) in .python-version and rebuild each venv once: new venvs then point at the real folder " +
             "and kept 0 (fsfxing2023-wq). Keep worktrees whose node_modules is a junction out of TEMP and AppData, or run their tests from a " +
             "terminal outside the app (bronze1man). These are yours to change: PoolWatch only gives this advice.",
    };

    private const string Guided = "PoolWatch sets the variables for your account when you say so, then shows the rest of the steps, with a prompt " +
                                  "for your AI agent. It never reinstalls, moves or deletes your tools or files.";

    private static string Names(LinkFinding f) => string.Join(" and ", f.EnvSettings.Select(s => s.Name));

    // ---------------------------------------------------------------- the questions before a change

    public const string ReplaceTitle = "Replace a PATH entry";

    public static string ReplaceQuestion(LinkFinding f) =>
        $"Replace this entry on {Where(f)}:\r\n\r\n    {f.Entry.Trim()}\r\n\r\nwith the folder its link leads to:\r\n\r\n    {f.Target}\r\n\r\n" +
        "Every other entry stays as it is. Active changes can put it back. " + Restart;

    public const string SetTitle = "Set environment variables";

    public static string SetQuestion(LinkFinding f) =>
        "Set these for your account:\r\n\r\n" + string.Join("\r\n", f.EnvSettings.Select(s => $"    {s.Name} = {s.Value}" + (s.Seen is { } seen ? $"  (now {seen})" : ""))) +
        "\r\n\r\nPoolWatch sets only these and does not reinstall, move or delete anything: the steps for that come next. Active changes can put " +
        "them back.";

    // ---------------------------------------------------------------- the steps left to the user, and the prompt

    public const string StepsTitle = "What is left to do";
    public const string CopyPrompt = "Copy prompt for your AI agent";

    /// <summary>What is left after PoolWatch set the variables: the steps, numbered, for the row's tool.</summary>
    public static IReadOnlyList<string> Steps(LinkKind kind) => kind switch
    {
        LinkKind.Uv =>
        [
            "Quit the Claude and Codex apps fully, and open a new terminal outside them, so it sees the new variables.",
            "Reinstall each Python you use (uv python install 3.12, and so on). 0uternet copied the old folders instead and ran uv python install " +
            "with --force --offline for each version.",
            "Rebuild each venv with an exact version (uv venv -p 3.12.13), or delete .venv and run uv sync. Editing pyvenv.cfg alone is not enough: " +
            "uv's launcher in the venv keeps the old path (0uternet).",
            "Reinstall each uv tool (uv tool list shows them), so its launcher points at the new Python.",
            @"Once nothing uses them, remove the old links in %APPDATA%\uv\python (rmdir removes the link only, not what it leads to).",
            "Start the Claude and Codex apps again.",
        ],
        LinkKind.Pnpm =>
        [
            "Quit the Claude and Codex apps fully, and open a new terminal outside them, so it sees the new variable.",
            "Put the new pnpm home on your PATH: run pnpm setup and check that it names the new folder, or add the folder to your PATH yourself.",
            "Reinstall each global package (pnpm add -g and its name). A terminal opened before the change still lists the old ones with pnpm ls -g.",
            @"Once nothing needs it, remove the old %LOCALAPPDATA%\pnpm entry from your PATH. pnpm's own settings stay in %LOCALAPPDATA%\pnpm\config.",
            "Start the Claude and Codex apps again.",
        ],
        _ =>
        [
            "Quit the Claude and Codex apps fully, and open a new terminal outside them, so it sees the new variable.",
            "Run codex update (or the Codex install command again): it makes its link in the new folder, outside AppData, and puts that folder on your PATH.",
            @"Remove the old %LOCALAPPDATA%\Programs\OpenAI\Codex\bin entry from your PATH, or let PoolWatch's AppData links window replace it.",
            "Start the Claude and Codex apps again.",
        ],
    };

    /// <summary>The steps window's text: what PoolWatch set, then the numbered steps.</summary>
    public static string StepsText(LinkFinding f, IReadOnlyList<EnvSetting> set)
    {
        var sb = new StringBuilder("PoolWatch set these for your account:\r\n\r\n");
        foreach (var s in set) sb.Append("    ").Append(s.Name).Append(" = ").Append(s.Value).Append("\r\n");
        sb.Append("\r\nThe rest is yours to do (PoolWatch never reinstalls, moves or deletes your tools or files). Your AI agent can do it with you: ")
          .Append("the button below copies a prompt for it.\r\n\r\n");
        var steps = Steps(f.Kind);
        for (int i = 0; i < steps.Count; i++) sb.Append(i + 1).Append(". ").Append(steps[i]).Append("\r\n");
        sb.Append("\r\nPut back in Active changes sets the variables back as they were. What you reinstalled stays where it is.");
        return sb.ToString();
    }

    /// <summary>
    /// The prompt for the user's AI agent: what the leak is, what PoolWatch set (each folder with the profile written as
    /// %USERPROFILE%, so no user name is in it), and the steps, asking before each change. Nothing else of the PC is in it.
    /// </summary>
    public static string Prompt(LinkFinding f, IReadOnlyList<EnvSetting> set, string profile)
    {
        var sb = new StringBuilder();
        sb.Append("Please help me finish a fix that PoolWatch started on this Windows PC.\n\n");
        sb.Append("The problem: inside the Claude desktop app and the Codex desktop app (Microsoft Store packages), every open of a file or folder through ")
          .Append("a junction or symbolic link located under %APPDATA% or %LOCALAPPDATA% leaks one block of the kernel's non-paged pool (pool tag NtFC) ")
          .Append("until Windows restarts. It is a bug in bindflt.sys. The measurements and the fixes that measured 0 are in ").Append(LinkFinder.IssueUrl).Append(".\n\n");
        sb.Append("PoolWatch has set these user environment variables (leave them as they are):\n");
        foreach (var s in set) sb.Append("- ").Append(s.Name).Append(" = ").Append(LinkPaths.InProfileWords(s.Value, profile)).Append('\n');
        sb.Append("\nWhat is left, step by step. Ask me before you change, reinstall or delete anything, and tell me when to quit and restart ")
          .Append("the Claude and Codex apps. Run the commands from a terminal outside those apps if you can: programs outside them do not leak.\n");
        var steps = Steps(f.Kind);
        for (int i = 0; i < steps.Count; i++) sb.Append(i + 1).Append(". ").Append(steps[i]).Append('\n');
        return sb.ToString();
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
