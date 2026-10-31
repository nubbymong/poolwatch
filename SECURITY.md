# Security

PoolWatch always runs as administrator, so a weakness in it can matter more than in an ordinary app. Thank you for
reporting one privately.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/nubbymong/poolwatch/security/advisories/new) (Security tab → "Report a
vulnerability"). Only the maintainer sees the report; a fix is developed privately and published with an advisory.
PoolWatch's Report an issue window links to the same page.

Please include the PoolWatch version, your Windows build, what an attacker needs (a standard user? malware running as
you? local admin?), and the steps. Leave out personal data: do not attach an unredacted incident folder (see
"Privacy" in the README).

PoolWatch is maintained by one person in their spare time. Expect an acknowledgement within a week; fixes go into the
next release. Only the latest release is supported.

## Security model

The attacker that matters is a standard (unelevated) user, or malware running as you, trying to make the elevated
PoolWatch run their code, change the wrong thing, or leak data. One way to do that has no defence in 1.0.0: see the
first item under [Known limitations](#known-limitations).

- **Runs only from an admin-only place.** An elevated PoolWatch starts only from under `%ProgramFiles%` or
  `%ProgramFiles(x86)%`, and only where a standard user cannot change it: otherwise someone could replace the exe or a
  DLL next to it. Read only, it judges the access rules (the DACL and the owner) of every folder from its own up to
  Program Files, the rules its folder gives the files made in it, and the rules of `PoolWatch.exe` itself. Rules that
  cannot be read count as changeable, and so does an exe that is not there or that is itself a link (a symbolic
  link). Anywhere else (Desktop, Downloads, OneDrive) it refuses outright. The setup installs it to
  `%ProgramFiles%\PoolWatch` only. Three switches skip this check, whoever passes them: `--allow-any-location` (CI
  and development), `--ui-smoke` (CI's UI test) and the token-leak test's hidden helper (`--token-leak-test`, which
  a PoolWatch that passed the check starts by its own path). None is for a copy you use, and each still needs an
  administrator start: your Yes on the prompt. Approve a PoolWatch administrator prompt only when you started
  PoolWatch yourself: **Show more details** on the prompt shows the program's location, which should be in
  `C:\Program Files\PoolWatch`.
- **Who counts as a standard user** in that check, and in putting back a clear task (below):
  - always: Everyone, Authenticated Users, Users, INTERACTIVE, BATCH, LOCAL, CONSOLE LOGON, REMOTE INTERACTIVE LOGON,
    This Organization and Local account;
  - the signed-in account, as its programs without administrator rights run: with UAC on (a split token), the own SID
    and enabled groups of the linked token, or the elevated token's when that one cannot be read; with UAC off, or as
    the built-in Administrator (no split token), the account's groups but not its own SID, since none of its programs
    runs without administrator rights;
  - never, even as that account: SYSTEM, LOCAL SERVICE, NETWORK SERVICE, TrustedInstaller, "Local account and member
    of Administrators group", and the groups UAC treats as elevated (Administrators, Power Users, Account Operators,
    Server Operators, Print Operators, Backup Operators, RAS Servers, Network Configuration Operators, and a domain's
    Domain Admins, Domain Controllers, Cert Publishers, Schema Admins, Enterprise Admins and Group Policy Creator
    Owners).

  A folder or file counts as changeable when a rule lets one of them add, write, append, delete, change the
  permissions or take ownership, when its owner is one of them, when it has no access rules at all, or when it holds a
  kind of rule PoolWatch does not know. A deny rule gives nothing.
- **Native DLLs** ship next to the exe in that folder (never unpacked to `%TEMP%`), and system DLLs are loaded from
  System32 only.
- **Its data folder** `%ProgramData%\PoolWatch` is admin-only: Administrators and SYSTEM full control, read for your
  account, inheritance off. It is opened without following links on every start; a link, a file, or a folder someone
  else owns or can change or whose permissions cannot be read there is renamed aside (`PoolWatch.untrusted-<time>`,
  the link itself, never its target) and a fresh folder is created with its permissions set at creation.
- **Every write and delete is checked against links.** In the data folder (or an explicit headless `--out` folder)
  every path goes through one check (`PathGuard.CheckReal`): never through a link, never next to the exe, never on a
  network drive or a ReFS volume. The few writes and deletes elsewhere check for links themselves: the desktop
  shortcut and an older version's data folder are opened without following links and their final path checked
  (`NoFollow`), and an older version's helper files in Program Files are checked folder by folder
  (`PathGuard.FirstLinkBelow`).
- **What it reads from places you can write to** is bounded, and links are skipped rather than followed: Startup-folder
  shortcuts (its own parser, not the shell), Claude Code transcripts (size and file budgets, links skipped),
  `pooltag.txt` (read in one bounded pass of at most 8 MB; a path that is itself a link is skipped), symbol files
  only from admin-only local folders, and an AI agent's answer file (below). Path settings must be plain local drive
  paths.
- **The AI agent's files** (Services tab, **Decide with your AI agent** and **Import answer**). The prompt
  (`poolwatch-services.md`) is written only where you pick in a Save dialog: into a new file, or over the file you
  agreed to replace only when that is a plain file with one name, never through a link, and never when a folder on the
  way is a link to another place. The answer (`poolwatch-services-answer.json`) is read only from the file you pick,
  at most 64 KB, never from a link in its own place or through a folder on the way that is a link; hard links are not
  refused on read. The answer is untrusted input: it must be exactly PoolWatch's format (version 1: what to turn off,
  what to keep, and a reason per name, as valid UTF-8 text), or the whole answer is refused, with why; reading it never
  throws. A name in it only ever picks an item from the list PoolWatch reads from the live PC again at that moment;
  nothing in it is run or opened, and its words are shown only as plain text. The prompt holds names that other
  programs choose (and any account that can register a scheduled task); they are written only as names in its tables,
  and the prompt's own instructions name only the products in PoolWatch's catalogue. Its redaction is best-effort, as
  an export's is: it reads each word whole, or not at all.
- **Records it acts on are checked again first.** The change records (`applied-changes.json`, `service-changes.json`)
  are admin-only, but each one is read as untrusted, size-bounded and validated, and checked against the live system
  right before PoolWatch puts a change back or the uninstaller acts on it. The same holds for its cached files
  (`cache\environment.json`,
  `cache\update-check.json`): a file with anything out of place is ignored. The service profiles in `settings.json`
  are read as untrusted too, since one can be applied at start without a click: at most 50 profiles of at most 200
  services each, a profile not in the form PoolWatch writes is left out, an entry for a protected service or one
  PoolWatch does not recognise is ignored, and a choice of profile or delay that cannot be read applies nothing. Each
  is said in the log.
- **Programs and pages it opens.** Programs are started by their full path, never through a shell association or the
  current folder. A web page is opened only when you click, only for an `https://` address PoolWatch knows (its
  repository, the credits, a release page, the private advisory page, and three reference pages setup cites, each a
  fixed address matched whole) or a new-issue address it built itself, and only through `explorer.exe`, so your
  browser runs as you, never as administrator.
- **The logon task** (optional "Start PoolWatch when you sign in") is only created for a copy installed under
  `%ProgramFiles%`. At every start PoolWatch checks an existing `\PoolWatch` task: if any of its actions starts
  something outside Program Files, or is not a program it can check, it says so and offers to point the task at the
  installed exe or remove it. This check goes by location only; the PoolWatch the task starts is then judged by the
  full check above.
- **The exit signal** (`Global\PoolWatch-Exit`), which the setup and the uninstaller use to ask PoolWatch to exit, can
  be opened only by Administrators and SYSTEM, so a program without administrator rights cannot make PoolWatch exit. A
  name someone else created first is not used.
- **One network request, optional: the update check.** About once a day, while "Tell me when a new version is out" is
  on (it is by default), the tray app sends one HTTPS GET for its own repository's latest release, to
  `https://api.github.com/repositories/1435312349/releases/latest`: the repository by its number, which stays with it
  through a rename or a transfer and is never given to another one. The first check waits until setup has been
  completed once. The request carries no sign-in, no cookies and nothing about the PC: only the User-Agent
  `PoolWatch/<version>` and an `Accept` header asking for GitHub's JSON (`application/vnd.github+json`). GitHub sees
  the IP address it comes from, and it goes through the PC's proxy when one is set. Redirects are not followed,
  the request gives up after 15 seconds, and an answer over 1 MiB is refused (unread when its length says so). Only a
  stable release is offered (a pre-release or a draft never is), and only when its page is that release's own page under
  `https://github.com/nubbymong/poolwatch/releases/tag/`. The check never downloads or runs anything: the notice only
  opens that page in your browser when you click. There is no telemetry, crash upload or symbol server.
- **Known-issue packs** (`knowledge/*.json`) are built into PoolWatch and change only with a release; PoolWatch does not
  download them. Each is still read as untrusted (at most 32 KB, strict JSON, every field checked, links only to
  github.com, learn.microsoft.com and support.microsoft.com). A pack holds text, pool tags, program names, numbers and
  links: nothing in it is run, and it cannot make PoolWatch suggest or change anything.

## What PoolWatch changes on your PC

PoolWatch watches and reads. This is every elevated or system-changing action it can take; everything else only reads,
or writes its own data folder. The changes that last are listed under **Active changes**, each of which can be turned
off or put back (the few that cannot fully come back say so there). Uninstalling puts them all back by default, except
those Active changes marks "Uninstalling does not put this back" (a change PoolWatch cannot put back, and a clear task
of yours that needs your yes first): the uninstaller lists those, with why, and leaves them as they are.

| What | When | How it is confirmed |
|---|---|---|
| Starts its own real-time kernel ETW session (process starts and exits) and restarts it if it is stopped | Always, while running | Running PoolWatch |
| Records a WPR trace (`wpr.exe`, instance "PoolWatch": kernel pool events, optionally Kernel-File and CPU sampling), at most 5 minutes; may end its own stuck `wpr.exe`, never someone else's recording | **Capture now** (tray menu), or on a pool alert when automatic captures are on (off by default; at most one an hour, never while the PC is saturated or in use); a headless run with `--capture-now` | The menu item, the setting, or the command line |
| Empties the standby list (`NtSetSystemInformation`, after enabling SeProfileSingleProcessPrivilege in its own token); no file or process is touched | **Clear now**, the automatic clear (off until you turn it on in setup or Settings), the "lag before / after a clear" experiment; a headless run with `--clear-cache` | The button, the setting, starting the experiment, or the command line |
| Runs a full clear right after an automatic light clear (one that empties only the low-priority part of the standby list) that freed little | Only with the automatic clear on, in "auto" mode, once, within the 30-minute spacing, and only when no full clear was measured yet or the last one freed real pool | The automatic-clear setting |
| Deletes **your own clear task**: a task in the Task Scheduler root that runs Sysinternals RAMMap with an `-E` option (RAMMap itself is never touched; PoolWatch does not ship it). Every definition is saved and read back before the first task is deleted, so it can be put back | Turning the automatic clear on in Settings; **Next** on setup's Standby cache step with a rule chosen, exactly when the step shows the note that a rule replaces your task (decided from the same scan; the chosen rule is written right after the task is removed); **Remove my clear task** (tray menu); or `--enable-auto-clear` | A Yes in a dialog that shows the task, or your Next with setup's note on screen (`--yes` / `--no` on the command line) |
| Puts back a clear task of yours that PoolWatch removed, from its saved definition, with the account it ran as; PoolWatch's automatic clear is turned off first, so the two never both run | **Put back** in Active changes; the uninstaller's put-back | Re-checked first: only RAMMap memory-clear programs given by their full path with no environment variable, the Task Scheduler root (a task path with `/` is refused), no task of that name now, at most 64 KB. Every part PoolWatch checks (the programs, their options, the start folder and the account) must be plain text, with no comment, CDATA, processing instruction or element inside, and no program, option or start folder may hold a line break or another control character (a tab may separate the options). Never one that ran with a saved password or carries its own access rules (its definition is saved in `removed-tasks\` for you to import instead). One whose RAMMap a standard user could change (the folders' or the file's permissions, judged as for PoolWatch itself in the security model), whose permissions cannot be read, that is not there now or that is itself a link, only after a warning you accept in the app, which names every such program; the uninstaller never puts that one back (saved for import) |
| Removes what an older PoolWatch's Clear cache setup installed: the two `\PoolWatch\Clear cache (...)` tasks (only if they run the old helper), the empty `\PoolWatch\` task folder, and three helper files in `%ProgramFiles%\PoolWatch` | Automatically, once, at the first elevated start that finds them (a line in the findings log, `findings.jsonl`); **Remove old clear-cache tasks** (tray menu) or `--remove-clear-cache` as the fallback | Automatic (it never touches anything else) |
| Changes Windows services: sets one to Disabled and stops it, or to Manual without stopping it; puts back the saved start type and starts it again; sets the Windows default start type | **Services** tab (**Turn off N services**; **Put back...**: put back the selected service, set it to its Windows default, or put all back), **Apply** in the AI agent's review (below), **Put back** and **Put all back** in Active changes, the uninstaller, `--service-off`, `--service-restore` | A dialog listing the services (and a second one for services of other apps PoolWatch does not recognise); the original is saved first, and a service whose original cannot be read or saved is not changed. It never stops or changes a service you did not choose: when a running service you did not choose depends on one you turn off, that one is set to Disabled and left running, and the result says so. A service already Disabled is never set to Manual, and Turn off leaves a service that is off already as it is and records nothing for it (unless PoolWatch changed it before and it was changed again since, so that Put back refuses it: Turn off then records it again, so Put back works). Security, Windows Update, input and audio, Hyper-V / WSL and core services are never offered. Putting back (on the Services tab or in Active changes, one or all) or setting to the Windows default a service that the profile applied at start lists, when that leaves it on, says that the profile would turn it off again and, once you confirm, takes out of that profile the services the question named (one turned off after the question was asked stays in it, and the result says so); a put-back that leaves the service off (that of a Windows-default change, or an original of Disabled) never changes the profile. `--service-restore` and the uninstaller never change profiles (see [Known limitations](#known-limitations)) |
| Turns off the services of your service profile chosen to apply at start that are on (sets each to Disabled and stops it, or to Manual without stopping it, as Turn off does; one that a running service the profile does not list depends on is set to Disabled and left running, and the log says so) | Each time the tray app starts, after the 1 to 15 minutes you chose; only from PoolWatch's own `settings.json` (never with `--config`), only when PoolWatch runs as administrator, and never in CI's UI test | No click at start: you opted in with **Apply this profile when PoolWatch starts** when you saved the profile. Each service goes through Turn off's gate and the live gate, and its original is recorded before it is changed, so it can be put back (nothing is changed while the record cannot be read). Never a protected service or one PoolWatch does not recognise. One already as Turn off leaves it (Disabled and stopped, or Manual for a service PoolWatch turns off to Manual) is left alone; any other service of the profile found at Manual, for example after a Windows update, is set to Disabled and stopped if it runs (unless a running service the profile does not list depends on it, as above), with no click. It reads the profile only when its turn comes, after any other service change, and a change made to the profile since PoolWatch started never makes it turn off more: a service added or another profile chosen waits for the next start. A put-back takes a service out of the profile in its own turn, so this run never turns it off again. A notice says what it turned off (a click on it opens Active changes), and Active changes lists each, with Put back |
| Enables or disables a non-Microsoft scheduled task, or a startup app (the `StartupApproved` switch in HKCU / HKLM that Task Manager uses) | On the **Tasks and startup apps** tab: **Don't start at sign-in** / **Start at sign-in again** for a startup app, **Turn this task off** / **Turn this task back on** for a task; **Apply** in the AI agent's review (below); **Put back** in Active changes; the uninstaller | Your click on a selected row; recorded before the switch is made, so it can be put back (nothing is switched when the record cannot be saved, and a switch that fails takes its record back), one switch at a time. Microsoft's own startup apps and tasks are refused |
| Ends a process | **End it now** on the Tasks and startup apps tab: an app busy in the background, or the running program of a startup app or a task, offered only when every process of it was matched by its own full path | One question naming it, with its processes (PID and start time), its program and, for a busy app, its command line, and saying it cannot be undone; each process is ended through a handle checked against its start time. Never Windows' own, security or service-host processes: if any process of it is one, nothing of it is ended |
| Turns off the services, startup apps and scheduled tasks an AI agent's answer names | **Apply** in the review that **Import answer** (Services tab) shows | The review lists every change first, and every refusal with its reason (protected services, anything not on this PC now, Microsoft's own startup apps and tasks). Only items the answer matched on the live PC are changed, each through the same path as the Services and Tasks tabs, recorded first; services of other apps PoolWatch does not recognise are asked about again by name. Each can be put back from Active changes, and the uninstaller puts them back |
| Writes the AI agent's prompt, `poolwatch-services.md`, outside the data folder | **Save the prompt...** in **Decide with your AI agent** (Services tab) | The Save dialog: only where you pick, never through a link (security model). **Copy to clipboard** writes no file |
| Sets the live `ForegroundLockTimeout` to 0 until you sign out (never the registry; PoolWatch never sets it back), so any app may take the focus: the token-leak guard, for the leak documented on Windows 11 24H2 and 25H2 builds | When the guard is on (off until you turn it on): at once when you turn it on in setup's Fixes step (with Next), a card in the Simple view or Settings, at each start of the tray app (or of a headless run with `--token-guard`) unless two leak tests in a row on this build were clean, and again if something resets it | Your switch and Next in setup, or your click on the card or in Settings; when Windows refuses from the background, a notice asks for one click. Setup and the card offer it only on evidence of the leak on this PC |
| Runs the token-leak test: a hidden PoolWatch helper starts 600 `cmd.exe /d /c rem` children and measures (usually about 20 seconds); no setting is changed. The result is saved in `cache\token-guard.json` | Setup on Windows 11 24H2 and later when no result is saved for this build; the token-leak watch, by itself, with the guard off, once setup was completed and Windows was updated since the last result (or none was saved): in the window, and every 30 minutes from the tray; and the guard at start (the tray app, or `--token-guard`) when it needs a test | Opening setup, the watch (check only: when it finds the leak, a notice says so and a click opens the guard's card or its switch in Settings; nothing is turned on), or the guard's setting |
| Applies the automatic clear, the token-leak guard and update notices at once (to the running PoolWatch and `settings.json`) | Setup (Next on its Standby cache and Fixes steps; update notices on its last step), a card, Settings' Save, **Turn off** in Active changes (update notices: setup and Settings only) | Your click |
| Creates or deletes the `\PoolWatch` logon task (highest privileges); points an unsafe existing one at the installed exe or deletes it | **Start when you sign in** (setup's Fixes step, with Next), **Start PoolWatch when I sign in** (Settings), **Turn off** in Active changes, the uninstaller; the check at start | Your click; a question for an unsafe task |
| Creates the desktop shortcut "ChatGPT (no shader cache)", the Codex shortcut: built in the data folder, checked with PoolWatch's own parser, written only as a new file and never through a link (the Desktop and each folder above it up to your profile must be real folders, except OneDrive's); deleted by **Remove** in Active changes and by the uninstaller only if it is still exactly what PoolWatch wrote | Setup's Fixes step (**Yes**, then Next) when the Codex app is installed, or **Create shortcut** in Settings when it is not on the desktop | Your Yes and Next, or your click. The launcher it runs, `chatgpt-no-shader-cache.ps1`, runs as you, not as administrator, through `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File`: PowerShell does not check the script's signature then (it is signed in releases), so what protects it is the admin-only install folder. It starts the app through Windows' app activation and changes nothing on the PC |
| Opens a web page in your browser: the GitHub issue form (Report an issue), the links in About, the pages setup cites, a release page (update notice), the private advisory page | Your click | Only the addresses described in the security model, through `explorer.exe`. Report an issue's address holds only the title, version and Windows; the rest goes through your clipboard, and nothing is public until you submit the form |
| Asks GitHub once a day whether a newer release is out | While the tray app runs, after setup was first completed, and while "Tell me when a new version is out" is on (on by default) | The setting, on setup's last step and in Settings (see the security model) |
| Looks at the PC for setup (reads only): Windows version values and the Insider channel in the registry, your Store package list, a few fixed paths for AI tools, Task Scheduler, each Dev Drive's trusted flag, and Microsoft Defender's real-time protection and Dev Drive performance mode through WMI (never Defender's exclusion list). The result is kept in `cache\environment.json` (no user names, no paths except drive roots) | The first start, **Run setup again...**, when the window opens with facts more than a day old or from another Windows build, and again after a change setup or a card makes (a read that has run for a minute is given up on; the facts from before stay) | Automatic (reads only). Defender advice is instructions only: PoolWatch never changes Defender settings |
| Writes `AGENTS.md` (a guide to its files for AI assistants) into the data folder, and `setup.json` (when setup was completed, hidden suggestions and feature cards) and `ui-state.json` (the view) | When missing or different; when they change | Automatic (its own files in its own folder) |
| Creates and locks `%ProgramData%\PoolWatch`; renames an unsafe existing one aside | Every elevated start | Automatic; noted in `poolwatch.log` and the findings log |
| Moves data from an older version's `%LOCALAPPDATA%\PoolWatch` (its own files only, never through a link): each file is copied, read back and checked, then deleted there; what it leaves is named in a note in that folder. Its `settings.json` is never copied as it is: at the first start that finds no `settings.json` in the data folder, its settings are read and checked like any settings load and taken over without service profiles | Once, after an upgrade from a version that kept data there (a run that could not finish is tried again at a later start, at most five runs) | Automatic |
| The setup: installs to `%ProgramFiles%\PoolWatch` only (there is no folder page, and a setup started with `/DIR=` for another folder stops before copying anything). An upgrade first asks the running PoolWatch to exit through the exit signal. "Start PoolWatch now" starts it with the setup's administrator rights | Running the setup | Windows' User Account Control prompt |
| The uninstaller, after "Are you sure?" and before any file is removed: asks PoolWatch for its list of changes (`--changes-dump` into the admin-only data folder, as `uninstall-list.json`, deleted once read), and reads that list only when PoolWatch made it; otherwise it touches nothing in the data folder. Started by hand, it shows what it will put back and what it cannot (with why), and that the token-leak guard's Windows setting stays until sign-out, and asks: putting back is the default. It stops PoolWatch through the exit signal (never a kill); removes the logon task and the desktop shortcut; puts back every recorded change it can unless you keep them (one it cannot stays as it is); removes no file while PoolWatch still runs; writes `uninstall-summary.txt`; says what it put back, or what it could not and why; deletes the data folder only when you ask and only when every change was put back. Names in its boxes are shortened; the log keeps them whole | Uninstalling. `/KEEPCHANGES` keeps the changes. A silent uninstall and `/DELETEDATA` put back without asking (a `/DELETEDATA` uninstall started by hand then says what it put back); `/DELETEDATA` also deletes the data folder | Started by hand, its question (putting back is the default; a question that cannot be shown means putting back), then the data question (default No) |

Setup's **Next** confirms what its Standby cache and Fixes steps apply, so setup guards it against a press meant for
something else:

- Next is held, greyed, for one second whenever Next or Back brings up either step, whenever the scan's results arrive
  while either step shows (on time or late), after a change on either step did not work, and after a switch or a
  Yes/No appears on the Fixes step (while a change runs, from when that change ends).
- Next on the Fixes step applies only the switches and Yes/Nos that were on screen when it was pressed.
- Enter acts once per press. The setup window ignores the repeats of a held Enter (Shift+Enter too), so holding Enter
  moves setup on by one step at most.
- While Next is greyed (a change running, the scan, or the one-second wait), setup refuses Next however it is pressed
  (a click, Enter or a held key), and the keyboard focus moves from Next to the page area, so Enter does not land on
  another button.

## How changes are checked

- A change to an elevated action, to code that reads what a standard user can write, `src/PoolWatch/Native/`, the
  update check, the redaction, the installer or the release workflow (the list in AGENTS.md) gets a separate security
  review before it merges: a reviewer that did not write the change (another person or an AI review agent) tries to
  break it, and the pull request records what was tried.
- Every pull request runs the unit tests, end-to-end scenarios on Windows runners, the installer test and a
  dependency review. GitHub code scanning (CodeQL), secret scanning with push protection, Dependabot and OpenSSF
  Scorecard watch the repository.
- The workflows' actions are pinned to exact commits, and NuGet packages are restored in locked mode from
  `packages.lock.json`.
- A release is built and signed by CI from a `v*` tag; every file is checked for a valid signature from the expected
  certificate and an RFC 3161 timestamp before anything is published, together with `SHA256SUMS.txt`.

## Known limitations

- **No defence against .NET start-up variables.** .NET reads a few environment variables when a program starts that
  can make it load extra code, such as start-up hooks and profilers. A program running as you can set them for your
  account without administrator rights, and PoolWatch does not check for them or switch them off, so PoolWatch would
  load that code with administrator rights the next time it starts: after you approve its administrator prompt, or,
  if it starts when you sign in, at sign-in with no prompt at all. PoolWatch claims no defence against this. A fix is
  the first item planned after version 1.0.
- **The admin-only check has limits** (the security model above):
  - Another user's own SID, and groups that only other users hold, are not counted: only the well-known groups and
    the signed-in account, as described. So with UAC off, a member of a group UAC treats as elevated who is not an
    administrator is not counted through that group. And an administrator's token that UAC did not split for another
    reason (a batch or service sign-in, which UAC does not affect) is read like UAC off, so the account's own SID does
    not count; PoolWatch's own logon task signs in interactively, so PoolWatch does not start that way.
  - Of the files, only the program's own rules are read. The other files next to it (PoolWatch's DLLs) are judged by
    their folder's rules for files, and subfolders of the install folder are not judged.
  - A folder link (a junction) on the way is judged by the rules it shows, but the parents of its target are not. Only
    an administrator can make one in Program Files.
  - The logon-task check and setup's "installed in Program Files" fact go by location only. A PoolWatch
    started from such a folder is still refused at start.
  - When PoolWatch or its uninstaller runs as another account than the signed-in one, the signed-in user counts only
    through the well-known groups: for example a standard user elevating with another administrator's password, or a
    management tool running the uninstaller as SYSTEM.
- **A clear task's definition is checked as PoolWatch reads it.** Task Scheduler's own XML parser could still read a
  definition differently. PoolWatch's rules (one XML namespace, at most one of each part, plain text only in every
  checked part, no control character in a path or an option) narrow that to how single plain-text values are encoded.
  The definitions are kept in PoolWatch's admin-only data folder.
- **The token-leak test can be cut short.** While it runs, the workaround is off by design (the test measures the leak
  the workaround removes). If PoolWatch is killed or crashes in native code during the test, no handler runs. The hidden
  test helper then sees that PoolWatch is gone and asks for one click to turn the workaround on for this logon, and the
  next start finds the test's marker and turns it on without testing. That logon stays unprotected until the next start
  when the helper goes too (for example "End process tree"), when PoolWatch is killed outside the helper's run, or when the
  click is declined. The helper knows PoolWatch by process id only, so if Windows reuses that id during the test the
  helper sees it as still running and asks nothing. Repeated kills leave every other logon unprotected: a start that
  acts on the marker records the build as untested, so the logon after it tests again.
- **Turning the token-leak guard off cannot take back the focus setting before you sign out.** It stops PoolWatch
  setting it again; the live setting it already made lasts until you sign out. Active changes and the uninstaller say
  so.
- **The token-leak watch starts the test by itself.** After a Windows update, with the guard off, PoolWatch runs its
  check-only test without a click (the limitation above about a test cut short applies to it too). It never turns the
  guard on by itself.
- **A service profile applied at start asks nothing at start.** Ticking its box when you saved the profile is the
  consent. The uninstaller does not change profiles, so a data folder kept through an uninstall applies the profile
  again under a later PoolWatch. `--service-restore` never takes a service out of the profile, so the profile turns it
  off again at PoolWatch's next start, unless the start type it put back is already as Apply leaves it (Disabled, or
  Manual for a service PoolWatch turns off to Manual).
- **A pre-created or re-created data folder is moved aside, including one whose permissions deny Administrators.** An
  entry at `%ProgramData%\PoolWatch` that is a link or a file, or a folder that someone else owns or can change or
  whose permissions cannot be read, is renamed to `PoolWatch.untrusted-<time>` (for a link, the link itself, never its
  target) and a fresh admin-only folder is created; what was moved aside is kept for you to inspect and delete. Two
  cases still stop the move, and PoolWatch then exits rather than use an unsafe folder: a process that holds the entry
  open without sharing delete (`FILE_SHARE_DELETE`), and a process that re-creates `%ProgramData%\PoolWatch` right
  after every move (PoolWatch moves it aside at most six times in one start, then gives up). Both need a running
  process racing each start.
- **Over-the-shoulder elevation.** PoolWatch runs as the account that approved the administrator prompt. When a standard
  user elevates with another administrator's password, the per-user facts (AI tools in the profile, Store packages) and
  the desktop that gets the Codex shortcut are that administrator's.
- **The AI agent's Save and Open dialogs run elevated.** They are Windows' own dialogs, shown by the elevated
  PoolWatch. A standard user can preselect the folder they open in (Windows' per-app list of recent folders), and the
  dialog's own menus use the user's file associations, elevated. PoolWatch writes and reads only the file picked, under
  the rules in the security model. A prompt cannot be saved, and an answer cannot be imported, on a mapped network
  drive or a `subst` drive letter.
- **The AI agent's review tells items apart by their text.** A name that only looks like another's (letters from
  another alphabet), or that imitates "name (place)", is not caught: the review shows a startup app's place and a
  task's folder on their own line. PoolWatch always acts on the exact item the answer's name matched on this PC, shows
  the change before making it, and the change can be put back.
- **Symbol folders** are checked as folders (owner and permissions), not file by file: put only admin-owned PDBs in
  them.
- **Redaction is best-effort.** It replaces your user name, machine name, home and TEMP paths, domain, SID and values
  that look like secrets (and, in Report an issue, other paths, GUIDs, volume serials, e-mail, IP and MAC addresses),
  but it cannot know every secret format, and the `.etl` trace is left out because it cannot be redacted reliably. An
  export keeps Claude Code session ids and project names (Report an issue leaves sessions out), and the names you gave
  your service profiles. Read an export before you post it. The AI agent's prompt has a redaction of its own, also
  best-effort, and shows every word before you save or copy it.
- **The incident folders are not redacted.** They are for you (readable by your account and administrators) and say
  so at the top.
