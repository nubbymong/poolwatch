# Security

PoolWatch always runs as administrator, so a weakness in it can matter more than in an ordinary app. Thank you for
reporting one privately.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/nubbymong/poolwatch/security/advisories/new) (Security tab → "Report a
vulnerability"). Only the maintainer sees the report; a fix is developed privately and published with an advisory.

Please include the PoolWatch version, your Windows build, what an attacker needs (a standard user? malware running as
you? local admin?), and the steps. Leave out personal data: do not attach an unredacted incident folder (see
"Privacy" in the README).

PoolWatch is maintained by one person in their spare time. Expect an acknowledgement within a week; fixes go into the
next release. Only the latest release is supported.

## Security model

The attacker that matters is a standard (unelevated) user, or malware running as you, trying to make the elevated
PoolWatch run their code, change the wrong thing, or leak data.

- **Runs only from an admin-only folder.** An elevated PoolWatch refuses to start from a folder you can write to without
  administrator rights (Desktop, Downloads, OneDrive): someone could replace the exe or a DLL next to it there. Install
  it to `%ProgramFiles%\PoolWatch`. `--allow-any-location` lifts this for CI and development only.
- **Native DLLs** ship next to the exe in that folder (never unpacked to `%TEMP%`), and system DLLs are loaded from
  System32 only.
- **Its data folder** `%ProgramData%\PoolWatch` is admin-only: Administrators and SYSTEM full control, read for your
  account, inheritance off. It is opened without following links on every start; a link, a file, or a folder someone
  else owns or can change there is renamed aside (`PoolWatch.untrusted-<time>`, the link itself, never its target) and
  a fresh folder is created with its permissions set at creation.
- **Every write and delete** goes through one policy: only inside the data folder (or an explicit headless `--out`
  folder), never through a link, never next to the exe, never on a network drive or a ReFS volume.
- **What it reads from places you can write to** is bounded, and links are skipped rather than followed: Startup-folder shortcuts
  (its own parser, not the shell), Claude Code transcripts (size and file budgets, links skipped), `pooltag.txt` (read
  in one bounded pass of at most 8 MB; a path that is itself a link is skipped), and symbol files only from admin-only
  local folders. Path settings must be plain local drive
  paths.
- **The logon task** (optional "Start at logon") is only created for a copy installed under `%ProgramFiles%`. At every
  start PoolWatch checks an existing `\PoolWatch` task: if any of its actions starts something outside the admin-only
  folders, or is not a program it can check, it says so and offers to point the task at the installed exe or remove it.
- **No network.** PoolWatch makes no network connections: no update check, telemetry, crash upload or symbol server.

## What PoolWatch changes on your PC

PoolWatch watches and reads. This is every elevated or system-changing action it can take; everything else only reads,
or writes its own data folder.

| What | When | How it is confirmed |
|---|---|---|
| Starts its own real-time kernel ETW session (process starts and exits) and restarts it if it is stopped | Always, while running | Running PoolWatch |
| Records a WPR trace (`wpr.exe`, instance "PoolWatch": kernel pool events, optionally Kernel-File and CPU sampling), at most 5 minutes; may end its own stuck `wpr.exe`, never someone else's recording | **Capture now**, or on a pool alert when `CaptureOnAlert` is on (off by default; at most one automatic capture an hour, never while the PC is saturated or in use) | The button, or the setting |
| Empties the standby list (`NtSetSystemInformation`, after enabling SeProfileSingleProcessPrivilege in its own token); no process is touched | **Clear cache now**, the automatic clear (off by default), the "lag before / after a clear" experiment | The button, the setting (turning it on runs the step below first), starting the experiment |
| Deletes **your own clear task**: a task in the Task Scheduler root that runs Sysinternals RAMMap with an `-E` option (RAMMap itself is never touched; PoolWatch does not ship it) | When you turn the automatic clear on, or **Remove my clear task**, or `--enable-auto-clear` | A Yes in a dialog that shows the task (`--yes` / `--no` on the command line) |
| Removes what an older PoolWatch's Clear cache setup installed: the two `\PoolWatch\Clear cache (...)` tasks (only if they run the old helper), the empty `\PoolWatch\` task folder, and three helper files in `%ProgramFiles%\PoolWatch` | Automatically, once, at the first elevated start that finds them (a line on the Findings timeline); **Remove old clear-cache tasks** or `--remove-clear-cache` as the fallback | Automatic (it never touches anything else) |
| Changes Windows services: stops one and sets it to Disabled or Manual; puts back the saved start type and starts it again; sets the Windows default start type | **Background services** page (Apply, Restore, Restore to Windows default), `--service-off`, `--service-restore` | A dialog listing the services (and a second one for unrecognised third-party services); the original is saved first. Security, Windows Update, input and audio, Hyper-V / WSL and core services are never offered |
| Enables or disables a non-Microsoft scheduled task, or a startup app (the `StartupApproved` switch in HKCU / HKLM that Task Manager uses) | **Disable** / **Enable** on the Background services page | Your click on a selected row; recorded on the timeline |
| Ends a process | **End process** for a heavy background app | A dialog with its identity, PID, start time, image and command line; ended through the handle checked against that start time. Never Windows' own, security or service-host processes |
| Sets the live `ForegroundLockTimeout` to 0 (never the registry), so any app may take the focus | The token-leak guard, only when `TokenGuardEnabled` is on (off by default): at logon when its test finds the leak, and again if something resets it | The setting; when Windows refuses from the background, a notice asks for one click |
| Runs the token-leak test: a hidden PoolWatch helper starts 600 `cmd.exe /d /c rem` children | At logon, when the guard is on and needs a test | The setting |
| Creates and locks `%ProgramData%\PoolWatch`; renames an unsafe existing one aside | Every elevated start | Automatic; noted in the log and on the timeline |
| Moves data from an older version's `%LOCALAPPDATA%\PoolWatch` (its own files only, never through a link) and deletes them there after 7 days | Once, after an upgrade from a version that kept data there | Automatic |
| Creates or deletes the `\PoolWatch` logon task (highest privileges); points an unsafe existing one at the installed exe or deletes it | The **Start at logon** checkbox; the check at start | The checkbox; a question for an unsafe task |

## Known limitations

- **The token-leak test can be cut short.** While it runs, the workaround is off by design (the test measures the leak
  the workaround removes). If PoolWatch is killed or crashes in native code during the test, no handler runs. The hidden
  test helper then sees that PoolWatch is gone and asks for one click to turn the workaround on for this logon, and the
  next start finds the test's marker and turns it on without testing. That logon stays unprotected until the next start
  when the helper goes too (for example "End process tree"), when PoolWatch is killed outside the helper's run, or when the
  click is declined. The helper knows PoolWatch by process id only, so if Windows reuses that id during the test the
  helper sees it as still running and asks nothing. Repeated kills leave every other logon unprotected: a start that
  acts on the marker records the build as untested, so the logon after it tests again.
- **A pre-created or re-created data folder is moved aside, including one whose permissions deny Administrators.** An
  entry at `%ProgramData%\PoolWatch` that PoolWatch did not make — a plain folder, or a link (then the link itself, never
  its target) — is renamed to `PoolWatch.untrusted-<time>` and a fresh admin-only folder is created; the moved-aside
  folders are kept for you to inspect and delete. Two cases still stop the move, and PoolWatch then exits rather than use
  an unsafe folder: a process that holds the entry open without sharing delete (`FILE_SHARE_DELETE`), and a process that
  re-creates `%ProgramData%\PoolWatch` right after every move (PoolWatch moves it aside at most six times in one start,
  then gives up). Both need a running process racing each start.
- **Symbol folders** are checked as folders (owner and permissions), not file by file: put only admin-owned PDBs in
  them.
- **Redaction is best-effort.** It replaces your user name, machine name, home and TEMP paths, domain, SID and values
  that look like secrets, but it cannot know every secret format, and the `.etl` trace is left out because it cannot be
  redacted reliably. Read an export before you post it.
- **The incident folders are not redacted.** They are for you (readable by your account only) and say so at the top.
