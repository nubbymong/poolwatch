using System.Text;
using PoolWatch.Core.Util;

namespace PoolWatch.Core.Links;

/// <summary>
/// The AppData links window in plain words: each row's three columns, its details (what it is, what it costs, what fixes
/// it, who measured it), the question before a change, the steps that are left to the user after the guided fix, the
/// prompt for the user's AI agent, and the hints above the list (the size of NtFC's blocks, Fast Startup, the apps'
/// programs still running from before a change). The costs and fixes are the measurements in anthropics/claude-code
/// issue 96870 and the threads it links, each with the GitHub name of whoever made it. Pure, so the Linux tests read
/// every word.
/// </summary>
public static class LinkWords
{
    public const string WindowTitle = "Links under AppData";

    /// <summary>The account's local AppData folder as the window and the prompt write it, its variable: words only, never a path the code opens.</summary>
    public const string LocalVariable = "%LOCALAPPDATA%";
    public const string Lead = "Inside the Claude and Codex desktop apps, every open through a junction or link under AppData keeps a small " +
                               "block of the kernel pool until Windows restarts. These are the links PoolWatch found that the apps' tools go through.";
    public const string NothingFound = "PoolWatch found no link under AppData on your PATH, in uv, pnpm or the Codex command-line tool, in Claude Code's " +
                                       "temp folder or in the apps' data folders.";
    public const string NotInstalled = "Neither the Claude nor the Codex desktop app is installed for your account, so PoolWatch did not look: " +
                                       "programs outside those apps keep nothing.";
    public const string Looking = "Looking for links under AppData...";
    public const string Restart = "Quit the Claude and Codex apps fully (Quit in the tray too) and start them again: they see a new PATH or variable " +
                                  "only then. What leaked so far stays until a Restart: a Shut down with Fast Startup on keeps it.";

    public const string WhatColumn = "What", WhereColumn = "Where", FixColumn = "What fixes it";

    /// <summary>The button under the list for the selected row: what it does, or "Fix" greyed when it does nothing.</summary>
    public static string Button(LinkFinding f) =>
        !f.Actionable ? "Fix"
        : f.HasSteps ? "Show the steps..."
        : f.Kind == LinkKind.PathEntry ? "Replace on PATH"
        : f.Kind == LinkKind.PathEntryGone ? "Remove from PATH"
        : "Set the variables...";

    public const string LookAgain = "Look again", Close = "Close";

    /// <summary>The row's first column.</summary>
    public static string What(LinkFinding f) => (f.Kind switch
    {
        LinkKind.PathEntry or LinkKind.PathEntryGone => f.Entry.Trim(),
        LinkKind.WinGetLinks => $"winget's Links folder ({Count(f.Count, "link")})",
        LinkKind.Uv => f.Moved ? "uv's old Python links" : $"uv's Pythons ({Count(f.Count, "link")})",
        LinkKind.Pnpm => "pnpm's global packages",
        LinkKind.Codex => "The Codex command-line tool",
        LinkKind.ClaudeTemp => "Claude Code's temp folder",
        LinkKind.AppFolder => $"The {f.Tool} app's data folder",
        LinkKind.Venv => f.Tool is { } tool ? $"{tool}'s Python on PATH" : "A venv's Python on PATH",
        LinkKind.Hook => $"Claude Code hooks that start Python ({Count(f.Count, "hook")})",
        _ => "Links in your projects",
    }) + (f.InApp is { } app ? $", in the {app} app's own copy" : "");

    /// <summary>The row's second column.</summary>
    public static string Where(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry or LinkKind.PathEntryGone or LinkKind.WinGetLinks or LinkKind.Venv => f.Scope == EnvScope.User ? "your PATH" : "the machine's PATH",
        LinkKind.Hook => "your Claude Code settings",
        LinkKind.Projects => "your projects",
        _ => f.Link,
    };

    /// <summary>The row's third column.</summary>
    public static string Fix(LinkFinding f) => f.InApp is not null ? "Advice only" : f.Kind switch
    {
        LinkKind.PathEntry => f.Target is not null ? "Replace it with the folder it leads to" : "Change it yourself",
        LinkKind.PathEntryGone => f.Scope == EnvScope.User ? "Remove it from your PATH" : "Remove it yourself",
        LinkKind.Uv => f.EnvSettings.Count > 0 ? "Move uv out of AppData" : "Rebuild what still uses them",
        LinkKind.Pnpm => f.EnvSettings.Count > 0 ? "Move pnpm out of AppData" : "Reinstall its global packages",
        LinkKind.Codex => f.EnvSettings.Count > 0 ? "Install it outside AppData" : "Run its update again",
        LinkKind.ClaudeTemp => f.EnvSettings.Count > 0 ? "Move it out of AppData" : "Make it a plain folder",
        LinkKind.AppFolder or LinkKind.Venv or LinkKind.Hook => "Steps to follow",
        _ => "Advice only",
    };

    /// <summary>The details under the list: what it is, what it costs, what fixes it and what PoolWatch does.</summary>
    public static string Details(LinkFinding f)
    {
        var sb = new StringBuilder();
        sb.Append(What(f)).Append(" (").Append(Where(f)).Append(')').Append("\r\n\r\n");
        sb.Append("What it is: ").Append(Is(f)).Append("\r\n\r\n");
        sb.Append("What it costs: ").Append(Costs(f)).Append(BlockWords(f)).Append("\r\n\r\n");
        sb.Append("What fixes it: ").Append(Fixes(f)).Append("\r\n\r\n");
        sb.Append("Read more: ").Append(f.Kind == LinkKind.ClaudeTemp ? LinkFinder.TempLoopUrl : LinkFinder.IssueUrl);
        return sb.ToString();
    }

    /// <summary>" Each open through it keeps a block of 352 bytes." when the link's reparse data was read; else nothing.</summary>
    private static string BlockWords(LinkFinding f) => f.Block is { } b ? $" Each open through this link keeps a block of about {b} bytes." : "";

    private static string Is(LinkFinding f) => (f.InApp is { } app
        ? $"this is in the {app} app's own copy of AppData, where a program the app started made it; programs outside the app do not see it. "
        : "") + f.Kind switch
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
        LinkKind.ClaudeTemp => $"Claude Code keeps its temp files and scratchpads in {f.Link}, and here that folder is a junction or link" +
                               (f.Target is { } t ? $" to {t}." : "."),
        LinkKind.AppFolder => $"the {f.Tool} app keeps its data in {f.Link}, and that folder is a junction or link" + (f.Target is { } to ? $" to {to}" : "") +
                              " (to keep the data on another drive, say). It is on the app's own busiest path.",
        LinkKind.Venv => $"{f.Entry.Trim()} on {Where(f)} is the Scripts folder of a venv" + (f.Tool is { } tool ? $" that {tool} made" : "") +
                         $", and the Python it runs (its home in pyvenv.cfg) is reached through {f.Home}, a link under AppData. Every python, pip or tool " +
                         "command a program of the Claude or Codex app runs from it goes through that link.",
        LinkKind.Hook => $"{Count(f.Count, "hook")} in your Claude Code settings start Python ({string.Join("; ", f.Names)}), and Python here is likely " +
                         "reached through a link under AppData (uv's Pythons are).",
        _ => "a node_modules that is a junction, a venv whose home in pyvenv.cfg is one of uv's links, or a .python-version without an exact patch " +
             "(3.12 rather than 3.12.13) go through a link under AppData the same way. PoolWatch does not look inside your projects. Links outside " +
             "AppData kept nothing in the measurements (0uternet), so projects in Documents or OneDrive are not affected by this cause; OneDrive's " +
             "cloud files inside the apps were not measured.",
    };

    private static string Costs(LinkFinding f) => f.Kind switch
    {
        LinkKind.PathEntry => "every program a terminal or tool inside the Claude or Codex app starts, and every command it looks up, opens the folders on " +
                              "PATH. Through a link under AppData each open keeps a block: about one for every program Git Bash starts, wherever the entry " +
                              "is on PATH (DraftingDreamer), and two for each command lookup that reaches it (peterlau03).",
        LinkKind.PathEntryGone => "the folder the link leads to is gone, as an uninstall can leave it, and every command lookup that reaches this entry " +
                                  "still keeps blocks: 120 for 30 runs of a command that was not found (thyhellcat). Where the entry is on PATH does not " +
                                  "matter, and a command that is not found looks in every entry (thyhellcat, DraftingDreamer).",
        LinkKind.WinGetLinks => "each run of a tool through one of these links keeps a block for each open and each look at the file (thyhellcat). A lookup " +
                                "that finds nothing here keeps nothing, and neither do the app aliases in WindowsApps.",
        LinkKind.Uv => "each Python start through these links keeps blocks: 64 for python -c pass and about 650 with a few imports (0uternet), about " +
                       "600 for each hook that runs Python (peterlau03), and a pytest run kept about 1,160 a second (0uternet).",
        LinkKind.Pnpm => "about 25 blocks for each run of a global tool (peterlau03).",
        LinkKind.Codex => "its bin folder on PATH costs as any PATH entry through a link does: about one block for every program Git Bash starts " +
                          "(DraftingDreamer). Each codex update puts the link back on PATH (peterlau03).",
        LinkKind.ClaudeTemp => "with its temp folder a junction, Claude Code in the Claude app retries a failing open tens of thousands of times a second, " +
                               "and each try keeps a block of about 96 bytes: about 230 MB a minute (jallers), 282 to 330 MB a minute (alphamr123). " +
                               "As a plain folder it stopped (samZoneN).",
        LinkKind.AppFolder when f.Tool == LinkApps.Claude =>
            "the Claude app's data folder as a junction to another drive grew the kernel pool by about 2.2 GB an hour; an ordinary folder stopped it, " +
            "and Cowork's mkdir errors with it (Aviv5374).",
        LinkKind.AppFolder => "the Codex app's data folders as junctions grew it by 150 to 212 MB a minute, and ordinary folders stopped it (S4kuzy0); " +
                              "each open through such a junction kept 240 bytes (zjr-cyber). With three of them junctions, every one had to go (lingbai-i).",
        LinkKind.Venv => "188 blocks for each python -c pass through PlatformIO's venv (SynoSoftware); 383 for each start of a Hermes Agent tool, and " +
                         "18 GB in 3 days (eugenetsaplin).",
        LinkKind.Hook => "each hook run that starts Python through uv's link keeps about 600 blocks (peterlau03). Hooks run on every tool call, so they " +
                         "are often the largest part: 17,366 hook starts in a day came to about 1.4 GB (estoll37).",
        _ => "a test run in a worktree under TEMP whose node_modules was a junction grew the kernel pool by about 15 GB in an hour (bronze1man), " +
             "though on two other PCs a junction under TEMP kept nothing (konrad-rzonca, fsfxing2023-wq), so TEMP is not reliably safe; a venv " +
             "on uv's link kept about 322 blocks for each python -c pass (morenogironadr).",
    };

    private static string Fixes(LinkFinding f) => f.InApp is not null ? InAppFix : f.Kind switch
    {
        LinkKind.PathEntry when f.Target is { } target =>
            $"put the folder the link leads to, {target}, in the entry's place: replacing such entries with their targets measured 0 (peterlau03, " +
            "thyhellcat, DraftingDreamer). PoolWatch changes only this entry and keeps every other one, and the value's kind, as they are. " +
            "If the tool later changes where its link leads (to switch versions, say), your PATH keeps the folder it leads to now: " +
            "Put back in Active changes puts the entry back. " + Restart,
        LinkKind.PathEntry => $"PoolWatch does not replace this entry: {f.WhyNot} You can change it yourself in Windows' Environment Variables window, " +
                              "to a folder that is not reached through a link under AppData.",
        LinkKind.PathEntryGone when f.Scope == EnvScope.User =>
            "remove the entry from your PATH: removed, it kept about nothing (DraftingDreamer). PoolWatch removes only this entry when you say so, " +
            "and keeps every other one, and the value's kind, as they are; Put back in Active changes puts it back while your PATH is still as " +
            "PoolWatch left it. It never deletes the link itself. " + Restart,
        LinkKind.PathEntryGone => "if the tool is gone, remove the entry from the machine's PATH in Windows' Environment Variables window. PoolWatch " +
                                  "removes entries only from your own PATH.",
        LinkKind.WinGetLinks => "no change that measured 0 is known for the folder itself. Leave the folder on your PATH: every portable app winget " +
                                "installed needs it, and a lookup that misses there keeps nothing (thyhellcat). A tool your agents run often keeps " +
                                "nothing when it is called from the folder winget installed it in, or from a terminal outside the Claude and Codex apps.",
        LinkKind.Uv when f.EnvSettings.Count > 0 => "set " + Names(f) + " to folders outside AppData, then reinstall your Pythons and rebuild your " +
            "venvs and uv tools: Python starts then kept 0 (peterlau03, 0uternet, estoll37). " + Guided,
        LinkKind.Uv => "rebuild the venvs and uv tools made before (uv venv -p with an exact version), then remove the old links (rmdir removes the " +
                       "link only): 0uternet did this.",
        LinkKind.Pnpm when f.EnvSettings.Count > 0 => "set PNPM_HOME to a folder outside AppData, then reinstall your global packages: that measured 0 " +
            $@"(peterlau03). pnpm's own settings stay in {LocalVariable}\pnpm\config. " + Guided,
        LinkKind.Pnpm => "reinstall your global packages so they are in pnpm's new home (peterlau03).",
        LinkKind.Codex when f.EnvSettings.Count > 0 => "set CODEX_INSTALL_DIR to a folder outside AppData, so the installer makes its link there " +
            "and codex update stops putting the one under AppData back (peterlau03). " + Guided,
        LinkKind.Codex => "run codex update again from a terminal outside the apps, so it makes its link in the new folder, then replace or remove " +
                          "the old entry on your PATH.",
        LinkKind.ClaudeTemp when f.EnvSettings.Count > 0 =>
            "set CLAUDE_CODE_TMPDIR to a plain folder outside AppData, then quit the Claude app fully and start it again: that measured 0 " +
            "(jallers, Ghostgfdsrhj, samZoneN). Claude Code then keeps its temp files and scratchpads there, the worktrees it makes in them " +
            "included. A plain folder in place of the junction stopped it too (samZoneN). It did not help where the leak came from the Claude " +
            "app's data folder instead (Aviv5374). " + Guided,
        LinkKind.ClaudeTemp => "with the Claude app closed, make the folder a plain folder instead of a junction (samZoneN).",
        LinkKind.AppFolder => "make it an ordinary folder again, with the app fully closed: keep the junction under another name, copy the data, " +
                              "check it, and keep the old copy until the app works, as zjr-cyber did. It needs room on drive C:. PoolWatch never " +
                              "moves an app's data: the steps come with a prompt for your AI agent.",
        LinkKind.Venv => "rebuild the venv on a Python outside AppData, keeping its packages (uv venv --allow-existing with that Python): it then " +
                         "kept 0 (SynoSoftware, eugenetsaplin). Editing pyvenv.cfg alone is not enough, because the venv's launchers keep the old " +
                         "path (0uternet, SynoSoftware). PoolWatch never changes another tool's venv: the steps come with a prompt for your AI agent.",
        LinkKind.Hook => "once uv's Pythons are outside AppData, have each hook start Python by its full path in the new folder: then 3 MB in 75 " +
                         "hours (estoll37). PoolWatch never changes your Claude Code settings: the steps come with a prompt for your AI agent. It " +
                         "reads only the settings file in your profile, not the hooks of plugins.",
        _ => "pin an exact Python version (3.12.13, not 3.12) in .python-version and rebuild each venv once: new venvs then point at the real folder " +
             "and kept 0 (fsfxing2023-wq). Keep worktrees whose node_modules is a junction out of TEMP and AppData, or run big test suites from a " +
             "terminal outside the app (bronze1man); CLAUDE_CODE_TMPDIR outside AppData moves Claude Code's scratchpads, and the worktrees in them, " +
             "too. What else measured 0: programs started from a normal terminal (peterlau03, konrad-rzonca), the Codex command-line tool " +
             "instead of the desktop app (S4kuzy0), and Python started from Claude Code's PowerShell tool (DraftingDreamer, one report). Docker " +
             "Desktop started from a session cost 144 MB an hour (estoll37, one report), while docker commands from a session kept nothing " +
             "(morenogironadr). These are yours to change: PoolWatch only gives this advice.",
    };

    private const string Guided = "PoolWatch sets the variables for your account when you say so, then shows the rest of the steps, with a prompt " +
                                  "for your AI agent. It never reinstalls, moves or deletes your tools or files.";

    /// <summary>What fixes a row in an app's own copy of AppData.</summary>
    private const string InAppFix = "PoolWatch changes nothing in an app's own copy. A variable it sets for another row reaches the app's programs " +
                                    "too, but what they already made here stays: once the tool is set up outside AppData, rebuild or remove this " +
                                    "from a terminal in the app (konrad-rzonca, fsfxing2023-wq, zjr-cyber).";

    private static string Names(LinkFinding f) => string.Join(" and ", f.EnvSettings.Select(s => s.Name));

    // ---------------------------------------------------------------- the questions before a change

    public const string ReplaceTitle = "Replace a PATH entry";

    public static string ReplaceQuestion(LinkFinding f) =>
        $"Replace this entry on {Where(f)}:\r\n\r\n    {f.Entry.Trim()}\r\n\r\nwith the folder its link leads to:\r\n\r\n    {f.Target}\r\n\r\n" +
        "Every other entry stays as it is. Active changes can put it back. " + Restart;

    public const string RemoveTitle = "Remove a PATH entry";

    public static string RemoveQuestion(LinkFinding f) =>
        $"Remove this entry from {Where(f)}:\r\n\r\n    {f.Entry.Trim()}\r\n\r\nIts link under AppData leads to a folder that is gone. Every other " +
        "entry stays as it is, and the link itself is not deleted. Active changes can put the entry back while your PATH is still as " +
        "PoolWatch left it. " + Restart;

    public const string SetTitle = "Set environment variables";

    public static string SetQuestion(LinkFinding f) =>
        "Set these for your account:\r\n\r\n" + string.Join("\r\n", f.EnvSettings.Select(s => $"    {s.Name} = {s.Value}" + (s.Seen is { } seen ? $"  (now {seen})" : ""))) +
        "\r\n\r\nPoolWatch sets only these and does not reinstall, move or delete anything: the steps for that come next. Active changes can put " +
        "them back.";

    // ---------------------------------------------------------------- the steps left to the user, and the prompt

    public const string StepsTitle = "What is left to do";
    public const string CopyPrompt = "Copy prompt for your AI agent";

    /// <summary>The steps left to the user: after PoolWatch set the variables (uv, pnpm, Codex, Claude Code's temp folder), or all of them (a data folder, a venv, the hooks).</summary>
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
            $@"Once nothing needs it, remove the old {LocalVariable}\pnpm entry from your PATH. pnpm's own settings stay in {LocalVariable}\pnpm\config.",
            "Start the Claude and Codex apps again.",
        ],
        LinkKind.Codex =>
        [
            "Quit the Claude and Codex apps fully, and open a new terminal outside them, so it sees the new variable.",
            "Run codex update (or the Codex install command again): it makes its link in the new folder, outside AppData, and puts that folder on your PATH.",
            $@"Remove the old {LocalVariable}\Programs\OpenAI\Codex\bin entry from your PATH, or let PoolWatch's AppData links window replace it.",
            "Start the Claude and Codex apps again.",
        ],
        LinkKind.ClaudeTemp =>
        [
            "Make the folder CLAUDE_CODE_TMPDIR is set to, if it is not there yet (in File Explorer, or mkdir in a terminal).",
            "Quit the Claude app fully: Quit in its tray menu, then check in Task Manager that no claude.exe is left. PoolWatch names any that " +
            "still run from before the change.",
            "Start the Claude app again: Claude Code now keeps its temp files and scratchpads in the new folder.",
            $@"Leave the old junction at {LocalVariable}\Temp\claude as it is, or, with the app closed, remove it (rmdir removes the link only) and make a plain folder there.",
        ],
        LinkKind.AppFolder =>
        [
            "Quit the app fully: Quit in its tray menu, then check in Task Manager that none of its programs is left.",
            "Check that drive C: has room for the data in the folder the junction leads to.",
            "Rename the junction (add .old to its name, say): this keeps it, and the data it leads to stays where it is.",
            "Make an ordinary folder with the old name, and copy everything from the folder the junction leads to into it.",
            "Check that the number of files and their total size match, then start the app and check that it works.",
            "Keep the old copy until you are sure. Then remove the renamed junction (rmdir removes the link only) and, if you want, the old copy.",
        ],
        LinkKind.Venv =>
        [
            "Quit the Claude and Codex apps fully, and open a new terminal outside them.",
            "If PoolWatch offers to move uv's Pythons out of AppData (the uv row), do that first, and install the Python this venv uses there.",
            "Rebuild the venv in place on that Python, keeping its packages: uv venv --allow-existing --python followed by the new python.exe (or an " +
            "exact version such as 3.13.5) and the venv's folder (SynoSoftware, eugenetsaplin).",
            "Check that the tool still works (for PlatformIO, pio --version). Do not use uv venv --clear while a program runs from the venv: it can " +
            "leave it broken (fsfxing2023-wq).",
            "Running the tool's installer again can put the old link back (the Hermes Agent installer does): PoolWatch looks again every hour.",
            "Start the Claude and Codex apps again.",
        ],
        LinkKind.Hook =>
        [
            "If PoolWatch offers to move uv's Pythons out of AppData (the uv row), do that first, and install the Python your hooks use there.",
            "In your Claude Code settings, change each hook that starts python, py or uv to start Python by its full path in the new folder, " +
            "as estoll37 did (the full path to python.exe, then -s and the hook's script).",
            "Quit the Claude app fully and start it again.",
        ],
        _ => [],
    };

    /// <summary>The steps window's text after the guided fix: what PoolWatch set, then the numbered steps, for the row's tool.</summary>
    public static string StepsText(LinkFinding f, IReadOnlyList<EnvSetting> set)
    {
        var sb = new StringBuilder("PoolWatch set these for your account:\r\n\r\n");
        foreach (var s in set) sb.Append("    ").Append(s.Name).Append(" = ").Append(s.Value).Append("\r\n");
        sb.Append("\r\nThe rest is yours to do (PoolWatch never reinstalls, moves or deletes your tools or files). Your AI agent can do it with you: ")
          .Append("the button below copies a prompt for it.\r\n\r\n");
        AppendSteps(sb, f.Kind, "\r\n");
        sb.Append("\r\nPut back in Active changes sets the variables back as they were. What you reinstalled stays where it is.");
        return sb.ToString();
    }

    /// <summary>The steps window's text for a row PoolWatch changes nothing for (a data folder, a venv, the hooks): what it is, then the numbered steps.</summary>
    public static string GuideText(LinkFinding f)
    {
        var sb = new StringBuilder(What(f)).Append(" (").Append(Where(f)).Append(")\r\n\r\n");
        sb.Append("PoolWatch changes nothing here: these steps are yours to follow, and your AI agent can follow them with you (the button below ")
          .Append("copies a prompt for it).\r\n\r\n");
        AppendSteps(sb, f.Kind, "\r\n");
        return sb.ToString();
    }

    private static void AppendSteps(StringBuilder sb, LinkKind kind, string newLine)
    {
        var steps = Steps(kind);
        for (int i = 0; i < steps.Count; i++) sb.Append(i + 1).Append(". ").Append(steps[i]).Append(newLine);
    }

    private const string Problem =
        "The problem: inside the Claude desktop app and the Codex desktop app (Microsoft Store packages), every open of a file or folder through " +
        "a junction or symbolic link located under %APPDATA% or " + LocalVariable + " leaks one block of the kernel's non-paged pool (pool tag NtFC) " +
        "until Windows restarts (a Shut down with Fast Startup on does not free it). It is a bug in bindflt.sys. The measurements and the fixes that " +
        "measured 0 are in " + LinkFinder.IssueUrl + ".\n\n";

    /// <summary>
    /// The prompt for the user's AI agent after the guided fix: what the leak is, what PoolWatch set (each folder with the
    /// profile written as %USERPROFILE%, so no user name is in it), and the steps, asking before each change. Nothing else
    /// of the PC is in it.
    /// </summary>
    public static string Prompt(LinkFinding f, IReadOnlyList<EnvSetting> set, string profile)
    {
        var sb = new StringBuilder();
        sb.Append("Please help me finish a fix that PoolWatch started on this Windows PC.\n\n").Append(Problem);
        sb.Append("PoolWatch has set these user environment variables (leave them as they are):\n");
        foreach (var s in set) sb.Append("- ").Append(s.Name).Append(" = ").Append(LinkPaths.InProfileWords(s.Value, profile)).Append('\n');
        sb.Append("\nWhat is left, step by step. Ask me before you change, reinstall or delete anything, and tell me when to quit and restart ")
          .Append("the Claude and Codex apps. Run the commands from a terminal outside those apps if you can: programs outside them do not leak.\n");
        AppendSteps(sb, f.Kind, "\n");
        return sb.ToString();
    }

    /// <summary>
    /// The prompt for a row PoolWatch changes nothing for: what the leak is, what was found (its folders in the words of
    /// %APPDATA%, %LOCALAPPDATA% and %USERPROFILE%; a hook's command is never in it), and the steps, asking before each
    /// change.
    /// </summary>
    public static string GuidePrompt(LinkFinding f, string profile)
    {
        var sb = new StringBuilder();
        sb.Append("Please help me stop a kernel memory leak on this Windows PC.\n\n").Append(Problem);
        sb.Append("What PoolWatch found: ").Append(f.Kind switch
        {
            LinkKind.AppFolder => $"the {f.Tool} app's data folder, {LinkPaths.InProfileWords(f.Link, profile)}, is a junction or link to another place. " +
                                  "I want it to be an ordinary folder again, with its data kept.",
            LinkKind.Venv => $"a venv's Scripts folder is on my PATH ({LinkPaths.InProfileWords(f.Link, profile)})" + (f.Tool is { } tool ? $", made by {tool}," : "") +
                             $" and the Python it runs is reached through {LinkPaths.InProfileWords(f.Home ?? "", profile)}, one of uv's links under AppData. " +
                             "I want the venv rebuilt on a Python outside AppData, with its packages kept.",
            _ => "hooks in my Claude Code settings (%USERPROFILE%\\.claude\\settings.json) start python, py or uv, and Python here is reached through " +
                 "uv's links under AppData. I want each hook to start Python by its full path outside AppData.",
        }).Append("\n\n");
        sb.Append("Step by step. Ask me before you change, copy, move or delete anything, and tell me when to quit and restart the apps. ")
          .Append("Run the commands from a terminal outside the Claude and Codex apps if you can: programs outside them do not leak.\n");
        AppendSteps(sb, f.Kind, "\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- the hints above the list

    /// <summary>
    /// What the average size of NtFC's blocks says, as a hint, never proof: the link whose blocks are that size
    /// (konrad-rzonca, fsfxing2023-wq), or the retry loop under Claude Code's temp folder at about 96 bytes (jallers).
    /// Null when the average matches nothing found.
    /// </summary>
    public static string? BlockHint(long average, IReadOnlyList<LinkFinding> found)
    {
        if (found.FirstOrDefault(f => f.Block is { } b && LinkBlocks.Matches(average, b)) is { } match)
            return $"NTFS records (NtFC) average {average} bytes each since Windows started, the size an open through {What(match)} keeps: it is likely " +
                   "where most of them come from (the size follows the link: konrad-rzonca, fsfxing2023-wq).";
        if (LinkBlocks.Matches(average, LinkBlocks.TempLoop))
            return $"NTFS records (NtFC) average {average} bytes each since Windows started, the size the retry loop keeps when Claude Code's temp folder is " +
                   "a junction (jallers).";
        return null;
    }

    /// <summary>The most NtFC can hold before the hints speak of it: under this, its average and what it carried over say little.</summary>
    public const long HintFromBytes = 256L << 20;

    /// <summary>
    /// Fast Startup and the last start (Kernel-Boot's boot type: 1 after a Shut down with Fast Startup on): what leaked
    /// before a Shut down is still held, and only a Restart frees it (DraftingDreamer). Null when there is nothing to say.
    /// </summary>
    public static string? BootHint(bool? fastStartup, int? bootType, long ntfcBytes)
    {
        if (bootType == 1 && ntfcBytes >= HintFromBytes)
            return $"Windows last started from a Shut down with Fast Startup on, so the {PlainWords.Amount(ntfcBytes)} of NTFS records (NtFC) held now may be from " +
                   "before it: only a Restart frees them (DraftingDreamer).";
        if (fastStartup == true)
            return "Fast Startup is on: to free what leaked, use Restart, not Shut down (DraftingDreamer).";
        return null;
    }

    /// <summary>
    /// The apps' programs that started before PoolWatch's change and still run, so do not see it yet: by app, with their
    /// program names (at most 4 each). PoolWatch never ends them. Null when there are none.
    /// </summary>
    public static string? StillRunning(IReadOnlyList<AppProcess> old)
    {
        if (old.Count == 0) return null;
        var apps = old.GroupBy(p => p.App).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var names = g.Select(p => p.Program).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return $"{Count(g.Count(), "program")} of the {g.Key} app ({string.Join(", ", names.Take(4))}{(names.Count > 4 ? " and more" : "")})";
        });
        return string.Join(" and ", apps) + " started before PoolWatch's change and do not see it yet: quit the app fully (Quit in its tray menu) " +
               "and check in Task Manager that they are gone. PoolWatch never ends them.";
    }

    /// <summary>The tray's notice when a change PoolWatch made for links under AppData came back, naming what came back.</summary>
    public static string CameBack(IReadOnlyList<LinkFinding> back) =>
        $"Back under AppData: {What(back[0])}" + (back.Count > 1 ? $" and {back.Count - 1} more" : "") +
        ". A tool's update or installer can do this. Click here to see it and fix it again.";

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
