# PoolWatch for technical readers

How it works is in [DESIGN.md](DESIGN.md); every change it can make, and its security model, in
[SECURITY.md](../SECURITY.md).

## Why it needs administrator rights

Reading the kernel pool by tag, seeing every program that starts, recording a trace and clearing the standby cache
all need administrator rights, so PoolWatch always asks for them. Windows shows its **User Account Control** prompt:

> **Do you want to allow this app to make changes to your device?**
>
> PoolWatch

Under the name, Windows shows **Verified publisher** and a name: it should be the name on the copyright line of
[LICENSE](../LICENSE). You see the prompt when you run the setup (it then names PoolWatch Setup), and when you start
PoolWatch yourself. With **Start PoolWatch when you sign in**, Windows starts it at sign-in through a scheduled task
with administrator rights, without the prompt. Either way the program that gets those rights is PoolWatch's launcher,
the install folder's `PoolWatch.exe`, which starts PoolWatch itself (`app\PoolWatch.exe`, below it) with an
environment it builds from Windows' own values (its folders, the machine's registry and your profile's folders) and
passes on none of the environment it was started with, so settings a program running as you left in your environment
(among them .NET's start-up variables) do not reach it.

What PoolWatch does with those rights. The first five happen on their own; the others only when you choose them, with
a click or a setting you turned on:

| What | When |
|---|---|
| Reads the pool tags, every process and its handles, and watches program starts | always, while it runs |
| Creates its own data folder, `C:\ProgramData\PoolWatch`, that only administrators can change, and moves aside one that someone else owns or can change, or a link; gives one only administrators can change PoolWatch's permissions (its rules alone when its owner cannot be set), and uses it as it is when not even those can be applied, or they still differ once applied, unless what PoolWatch makes in it could then be changed by someone else or cannot be made at all (then it stops); reads one that Administrators own as its owner may, whatever its rules say, and secures it so; never moves aside a folder it cannot read now (the look at it fails, which includes an open that is denied while the backup privilege cannot be turned on, or its rules shut administrators out while it is an administrator's alone, as for one SYSTEM or TrustedInstaller owns): it stops instead, naming it; when that folder cannot be made safe, it keeps its data in a folder of its own next to it (`PoolWatch.safe-...`) rather than stop, records that folder's name in `HKLM\SOFTWARE\PoolWatch`, and moves it back once the usual name is free or the usual folder is safe to use where it stands (setting that one aside, and deleting the record); a start on the usual folder also deletes a record whose folder is gone or no longer admin-only, and names such a folder in its notes (with any files named like its lists of changes left in it); while that folder cannot be read, PoolWatch keeps the record and stops rather than use another data folder. Only the tray app, a headless run and the uninstaller's cleanup move or record anything; any other PoolWatch process uses the folder as it stands (one whose rules are not exactly PoolWatch's only while they let no one else change what is made in it and administrators may make files in it), and makes the usual one only when nothing is there and no PoolWatch is running. Each process writes only in the very data folder it checked (told apart by its file ID), which it holds open from then on so that no one can rename or delete it while that process runs (each write also checks it just before; while another program's open keeps that hold out, that check is all that holds) | at every start |
| Removes the unused clear-cache tasks and helper files a pre-release PoolWatch installed | once, when it finds them |
| Checks the task that starts PoolWatch at sign-in; if it starts something outside Program Files, names its program through an environment variable, starts PoolWatch's program itself (`app\PoolWatch.exe`), an older PoolWatch that has no launcher or a file that is not there, is something PoolWatch cannot check or read, or is otherwise not exactly the task PoolWatch makes (this installed PoolWatch's launcher with `--tray`, as you, signed in interactively, with highest privileges), PoolWatch asks whether to replace it with its own (it and its sign-in trigger then stay on or off as they were, and one with no sign-in trigger gets PoolWatch's, turned off) or remove it, and changes it only on your answer (a headless run only says so). A task made by an earlier version starts the install folder's `PoolWatch.exe` (or, from a self-contained zip's copy, `PoolWatch-selfcontained.exe`), which is the launcher once this version is installed or unpacked there, and needs no change | at every start |
| Checks whether this Windows build has the token leak: starts a tiny Windows command 600 times in the background and measures, usually about 20 seconds; nothing is changed | by itself when setup reads the PC on Windows 11 24H2 and later, after a Windows update while the token-leak guard is off (the token-leak watch, once setup was completed), and when the guard starts |
| Clears the standby cache (no file and no program is touched) | **Clear now**, the automatic clear, or the "lag before / after a clear" experiment you start |
| Records a trace of up to 5 minutes with Windows' own `wpr.exe` | **Capture now**, or when an incident opens if you turn that setting on (off by default) |
| Turns background services, scheduled tasks or startup apps off or back on | your click on the Services tab or the Tasks and startup apps tab, **Apply** in the review of an AI agent's answer, or **Put back** |
| Turns off the services of your service profile that are on (never a protected service, and never one PoolWatch does not recognise) | each time PoolWatch starts, after the 1 to 15 minutes you chose, if you ticked **Apply this profile when PoolWatch starts** for that profile |
| Ends a program (**End it now**) | your click, after a question that names it |
| Creates the scheduled task that starts it at sign-in | your switch in setup, or your click in Settings |
| Replaces your own RAMMap clear task, or puts it back | your choice of a rule in setup's Standby cache step (it says so), Settings, or **Put back** |
| Sets Windows' focus setting that stops the token leak, at most until you sign out: turning the guard off puts it back when Windows allows it (if Windows does not, or the guard found it already changed, it can stay until you sign out) | the token-leak guard, when you turn it on |
| Creates the desktop shortcut "ChatGPT (no shader cache)", the Codex shortcut | your Yes in setup, or **Create shortcut** in Settings |
| Writes the AI agent's prompt, `poolwatch-services.md` | **Save the prompt...**, only where you pick |

Because it runs as administrator, it runs only from inside Program Files, and refuses a copy there whose folders or
file you could change without administrator rights (anywhere else, such as Downloads or the Desktop, it refuses
outright). Only the switches its tests use skip that check (see [Build from source](#build-from-source)). It never
writes through a file-system link (a junction or symbolic link) and never installs a driver.
[SECURITY.md](../SECURITY.md) lists every action and how each is checked, and its
[known limitations](../SECURITY.md#known-limitations).

## How it decides

Per pool tag it looks for **growth** (by default 200 MB in 10 minutes, 1 GB in 60 minutes, or non-paged pool over
5 GB and still rising), **retention** (still held 15 minutes after the activity stopped) and **release** (a sharp drop:
whatever exited or was deleted then was holding the memory). Suspects are ranked on direct charge (handles of the
matching type, quota pool), activity correlation (Spearman, with a lag), lifecycle (started at onset, exited at the
end, orphaned) and ancestry. Each suspect's confidence is *weak evidence*, *some evidence*, *strong evidence* or
*confirmed by a trace*, in the same words in the window, Copy summary, the reports and the exports (report.json keeps
their names, such as `StronglyCorrelated`, which are what PoolWatch reads back); PoolWatch never claims causation from
correlation alone. What the pool does from just before PoolWatch's own cache clear until the clear is measured 2 minutes
later is that clear's result, never a release or a fall. Growth is measured across PoolWatch's clears on one baseline:
what a clear freed is added back, so growth after a clear counts from the level it left and growth before it still
counts ([DESIGN.md, section 6](DESIGN.md#6-detection)). So a leak just over a growth rule that the clears keep trimming
can go unreported: at the default 10-second sampling up to about 4% over the hourly rule with hourly clears, and more at
a longer sampling interval. A release is raised across a clear only on what the tag fell after the clear was measured.
Section 6 has every limit.

The growth and clear stats (the Simple view's tiles and the Advanced view's panel) count on the same baseline: each
minute keeps the pool's change with what the clears freed added back, and what they freed, for 25 hours, in memory only.
A window adds its minutes up before it says Grew or Fell on its own, so a pool that only wobbles shows neither. A change
across a sleep or a stall is not counted, and that time is not measured, but a drop across a clear's interval is still
that clear's; all of it is timed on the clock that only goes forward, so setting the system clock back or forward
changes none of it. The rate is that change per hour measured, shown after 30 minutes. A clear whose purge fails
only after its 2-minute measurement still counts as cleared there ([DESIGN.md, section 11](DESIGN.md#11-ui)).

**Impact levels** (setup's "How your PC is impacted" and the Impact column in Pool causes) come from one rule
(`src/PoolWatch.Core/Impact/ImpactRules.cs`). Measured: the growth per hour with PoolWatch's own clears added back
(never the net change, which the clears keep small), over the window shown: None under 10 MB an hour, Low under 5 GB a
day, Medium under 10 GB a day, High from 10 GB a day; a fall is None, and there is no level until 30 minutes are
measured. GB is 2^30 bytes, as PoolWatch shows sizes. Before anything is measured (a first run), setup gives each known
cause a level from what it found: a Dev Drive or another ReFS drive, High; Microsoft Defender's real-time protection
with a Dev Drive that performance mode does not cover, Medium, and with no Dev Drive at all, Low; the Codex desktop
app installed, Low; services PoolWatch knows running, Low; the token leak found by PoolWatch's check, High. None is only
for what PoolWatch read and found clean, or a check that ran and found nothing; for the token leak it also covers a
Windows build without the leak and the token-leak guard on, since the leak cannot build up there. A cause where
PoolWatch could not read what its level depends on, or whose check could not run or gave no clear answer, has no level:
setup shows it as Not known, in grey, not None. That includes a drive PoolWatch could not read whole: a fixed drive, or
one of a type Windows reports as unknown, whose file system Windows could not read, and a ReFS drive Windows could not
say is a Dev Drive or not; the row names it. Setup lists the causes by level: High, Medium, Low, then Not known and a
check still running, then None. What PoolWatch measures for a cause wins over what it found.

**Costs on the Services and Tasks tabs** use the same four words and the same meter for something else: what a
service's or a program's processes cost the processor now (`EntryCost`). For a startup app or a task, "now" is the
latest sampling window; for an app busy in the background listed as a row of its own, its 30-minute average (one whose
process a startup app or task runs is that entry's row, with the latest window).

When a suspect runs under Claude Code, PoolWatch searches that session's transcripts on this PC (bounded) for the tool
call that started it, and names the session, project, workflow and sub-agent; under AI Code Conductor (an app that
runs several Claude Code sessions) it also names the session and its account profile. PoolWatch never sends any of it
(see [Privacy](../README.md#privacy)); an export keeps it, so remove it before you share one if it is private.
[An example incident report](example-incident/report.md), from a CI run of an earlier version (before reports
said "NOT REDACTED" at the top, and with the older confidence words: "strongly correlated" for strong evidence,
"correlated" for some evidence, "weak signal" for weak evidence).

## Where it stores data

Everything goes under `C:\ProgramData\PoolWatch\` (administrators can change it; your account can read it). A daily
folder, an incident or an export is deleted once nothing in it was written for 7 days by default. The days are counted
on the PC's own timer, never on its clock, from the check (every 6 hours) that first sees it as it is: for a file its
last-write time and size, for a folder that is not a link also the name, size and last-write time of every file in it
but a link or what is behind one, so rows added to a day's files count as a write into its folder, and a change of the
PC's clock never removes anything early. Only time the PC is on and awake counts, so data can stay longer, and the first
time this version runs, what is already there is kept the whole period from then:

- `data\<yyyyMMdd>\`: the CSV files (totals, tags, processes, handles, events, services, volumes, lag, load) and
  `findings.jsonl`, about 2-3 MB an hour (about 350-500 MB over the 7 days kept). A CSV file whose columns changed in
  an update, or whose open or write another program refuses, is passed over: its rows go on in its next numbered file
  (`totals-2.csv`, say). Rows that no file can take yet are kept (up to about 4 million characters in all the files,
  the oldest of the file that keeps the most dropped beyond that) and written once one can; rows still kept, or
  dropped, when PoolWatch stops are counted in `poolwatch.log` (when its sampler does not stop within 30 s, the log
  says instead that the stop neither writes nor counts the rows not written yet);
- `incidents\<id>\`: `report.md`, `report.json`, `timeline.csv`, `processes.csv`, `events.csv`, `timeline.png`, and
  `capture.etl` when a trace was taken (a trace can add far more than the CSV files);
- `settings.json` (your settings and service profiles), `poolwatch.log` (and, only for a run during which an append
  to the log kept being refused — another program held it so the line could not be written, or opening the log to add
  the line would otherwise have had to wait for another program, which PoolWatch gives up at once rather than wait —
  after the appends were tried again three times, a new file named
  `poolwatch-<date>-<time>-<process id>-<n>.log`, whose first line says why; it can be read once that run has stopped,
  its `.1` once it has rolled over at 2 MB; the newest five are kept, and any of another PoolWatch still running),
  `service-changes.json` and `applied-changes.json` (what Active changes needs to put a change back; the file keeps its
  older name, so an installed copy's records stay readable), `removed-tasks\`, `setup.json` (when setup was completed,
  and the suggestions and feature cards you hid), `ui-state.json` (Simple or Advanced view), `uninstall-summary.txt`
  (and `uninstall-summary-kept.txt` while a cleanup's summary could not be written there: the next cleanup writes it
  in), `uninstall-list.json` (only while the uninstaller runs), `boots\`, `cache\`, `exports\`, `reports\`, `requests\`
  (notes sent with `--mark` to the running PoolWatch);
- `AGENTS.md`: a guide to these files for an AI assistant. If you ask one to look at PoolWatch's data or an export,
  point it at this file first.

## Command line

```
PoolWatch.exe                       tray app (opens the window)
PoolWatch.exe --tray                tray app without the window (the sign-in task uses this)
PoolWatch.exe --headless [--duration <s>] [--interval <s>] [--config <settings.json>] [--out <dir>] [--verbose-csv]
PoolWatch.exe --mark "<note>"       put a note on the running PoolWatch's timeline
PoolWatch.exe --services-dump <file> | --tasks-dump <file> | --changes-dump <file>
PoolWatch.exe --service-off <name> | --service-restore <name|all>
PoolWatch.exe --stop-running        ask a running PoolWatch to exit, then end the token-leak test helpers still running
                                    from this exe (exit code 7: still running; 9: its private names could not be opened)
PoolWatch.exe --uninstall-cleanup [--keep-changes] [--append-summary]   the uninstaller's step: stop PoolWatch, end
                                    those helpers, put back its changes and write uninstall-summary.txt (--append-summary:
                                    add to it, as the uninstaller's second cleanup does) (exit code 6: its data folder could
                                    not be made safe; 7: still running; 8: something was not put back; 9: its private names
                                    could not be opened)
PoolWatch.exe --selftest --out <dir>   native collector checks against independent sources
```

`PoolWatch.exe` here is the install folder's, PoolWatch's launcher: it asks for administrator rights and starts
PoolWatch itself, `app\PoolWatch.exe`, with the same options, passing on the output and the exit code. Any unknown
option (for example `--help`) prints the full list. Every option that does work needs administrator rights, which
PoolWatch gets from its launcher, so from an administrator console run the install folder's `PoolWatch.exe`.
`app\PoolWatch.exe` started by itself without those rights hands its start to the launcher, which shows the
administrator prompt; started as administrator any other way (from an administrator's own console, or **Run as
administrator** on that file), it starts again through the launcher and does no work of its own (SECURITY.md, Known
limitations, says what such a start still gets). The launcher starts PoolWatch only from a folder in Program Files that
no one but an administrator can change, and only with administrator rights. A file named by a relative path is read from the
folder you started it in.

`--service-off` exits 13 when the service was not turned off (refused, not changed, or set to Disabled but not known
to have stopped) or the services could not be read; `--service-restore` exits 8 when a service was not put back (its
record is kept), PoolWatch holds no change of the service named ("PoolWatch has not changed ...: nothing to put back"),
or the services could not be read. Each result line says why. A service whose start type is back but that was not
started again is put back: that alone exits 0, and its line says what did not happen. `--service-restore all` with
nothing recorded says so and exits 0.

`--changes-dump` writes the Active changes list as JSON, one object per row, in camel case: among others `what` (the
change in one line), `title` and `detail` (as the window shows them), `group`, `canUndo` and `note`, `uninstallLeaves`
(why the uninstaller leaves it, when it does) and `putBackCaveat` (what stays for a while after the uninstaller put it
back: today only the token-leak guard's Windows setting, until sign-out, and only when the guard found it already changed
and did not change it itself; the setting the guard changed in this sign-in, `--uninstall-cleanup` puts back when Windows
allows it, as Turn off does). Names are written as they are. The uninstaller writes this list as `uninstall-list.json`
in the data folder, reads `what`, `uninstallLeaves` and `putBackCaveat` from it line by line, and deletes it.

### Exit codes

Every exit code of PoolWatch and of its launcher, which passes PoolWatch's on as it is. They are defined once, in
`src/PoolWatch/Engine/ExitCodes.cs`, which both are built from, and each value has one meaning. The uninstaller acts on
each code its cleanup can end with; a test holds this table, that file and the setup's script together.

| Code | Name | What it means | Given by | What the uninstaller does with it |
|---|---|---|---|---|
| 0 | `Done` | What was asked was done | every step | goes on to remove PoolWatch's files |
| 1 | `Failed` | An error stopped the step, which may have done only part of what was asked | every step | removes nothing, says an error stopped the cleanup and that it may have put back part of the changes, and offers Retry |
| 2 | `BadCommandLine` | An option PoolWatch does not know, or one without the value it needs: nothing was done | every step | removes nothing and says to repair PoolWatch with its setup |
| 3 | `AlreadyRunning` | Another PoolWatch is running | the tray app, `--headless` | (never given to it) |
| 4 | `ClearTaskKept` | A clear task of your own is still there, so the automatic clear stays off | `--enable-auto-clear`, `--remove-legacy-clear-task` | (never given to it) |
| 5 | `NotInAdminFolder` | Someone who is not an administrator could change PoolWatch's program, its launcher or a folder they are in: nothing was done | every step | removes nothing and says to make sure only administrators can change the install folder |
| 6 | `DataNotSafe` | PoolWatch could not use its data folder, nor a folder of its own in its place: nothing was written there | every step that uses the data folder | removes nothing and says to close what may have that folder open, then Retry |
| 7 | `StillRunning` | A PoolWatch still runs: nothing was changed | `--stop-running`, `--uninstall-cleanup` | removes nothing and says to close PoolWatch, then Retry |
| 8 | `NotAllPutBack` | Something was not put back (it stays listed, so a later PoolWatch can still put it back) | `--uninstall-cleanup`, `--service-restore` | goes on, and says what was not put back |
| 9 | `NamesNotSafe` | PoolWatch could not open its private names or take its one-instance mutex: nothing was done | the tray app, `--headless`, `--stop-running`, `--uninstall-cleanup` | removes nothing and says to Retry, or to restart Windows first |
| 10 | `NotStarted` | PoolWatch did not run: its launcher could not start it, or it could not start its launcher | the launcher; PoolWatch started on its own | removes nothing and offers Retry |
| 11 | `Refused` | PoolWatch refused the start (without administrator rights from its launcher, or with .NET's start-up variables) | PoolWatch | removes nothing and offers Retry |
| 12 | `OtherDataFolder` | The cleanup ran (as 0 or 8), on the folder PoolWatch kept its data in while it could not use the usual one | `--uninstall-cleanup` | goes on, deletes neither folder, and says so |
| 13 | `NotAllTurnedOff` | A service was not turned off, or the services could not be read | `--service-off` | (never given to it) |
| 14 | `ResultNotWritten` | The token-leak guard's hidden helper could not write its result | the guard's helper | (never given to it) |
| 15 | `ChecksFailed` | A check of the self-test failed (`selftest.json` says which) | `--selftest` | (never given to it) |

The uninstaller treats any other code (a PoolWatch that crashed, say) as a cleanup that may have put back part of the
changes, and removes nothing. What `--stop-running` (run by the setup) and `--changes-dump` (run by the uninstaller)
end with is only logged. The setup's own codes are Inno Setup's (its documentation's "Setup Exit Codes"), and two of
Windows': 3010 when it finishes at the next restart (`ERROR_SUCCESS_REBOOT_REQUIRED`), and 1603 when Windows could not
be given its files for that restart (`ERROR_INSTALL_FAILURE`).

## Verify a release

Each release has `SHA256SUMS.txt` with the SHA-256 of the setup and both zips. Every PoolWatch program, the setup and
its uninstaller are signed (Authenticode, with an RFC 3161 timestamp); the other DLLs are Microsoft's, signed by
Microsoft. In PowerShell:

```powershell
(Get-FileHash .\PoolWatch-v1.0.0-setup-win-x64.exe -Algorithm SHA256).Hash   # compare with SHA256SUMS.txt
$sig = Get-AuthenticodeSignature .\PoolWatch-v1.0.0-setup-win-x64.exe
$sig.Status                          # must be: Valid
$sig.SignerCertificate.Thumbprint    # must be: 00EC0DEE208F91785D253C0731E28249C68053FD
```

The hashes in `SHA256SUMS.txt` are in lower case and `Get-FileHash` prints upper case: the case does not matter. A
changed file still names its signer, so check `Status` as well as the thumbprint. When the certificate is renewed, its
thumbprint changes; this section then shows the new one.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The code builds on any OS
(`EnableWindowsTargeting`) but runs only on Windows.

```
dotnet build PoolWatch.slnx -c Release
dotnet test tests/PoolWatch.Tests -c Release
dotnet test tests/PoolWatch.AppTests -c Release     # Windows only: the app layer, with fakes for every system change
dotnet publish src/PoolWatch/PoolWatch.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -o publish/fd/app
dotnet publish src/PoolWatch/PoolWatch.csproj -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/sc/app
dotnet publish src/PoolWatch.Launcher/PoolWatch.Launcher.csproj -c Release -r win-x64 -p:PublishAot=true -p:RestoreLockedMode=false -o publish/launcher
```

Each build is laid out as it is installed: PoolWatch itself in `app\`, and the launcher above it. The launcher's
publish compiles it to native code as `PoolWatch.exe` and needs the C++ build tools; copy it into both builds,
`publish/fd` and `publish/sc`, as CI does (each release zip also carries it as `PoolWatch-selfcontained.exe`). Package
versions are locked (`packages.lock.json`); CI restores in locked mode, except for the launcher's native publish, whose
compiler packages come at the SDK's own known versions (as the self-contained build's runtime packs do), which no lock
file records; on Windows NuGet checks every package's signature as it restores. The SDK is not pinned (there is no
`global.json`): CI takes the newest .NET 10 SDK (`10.0.x`), so each build gets the patch that carries .NET's latest
security fixes, and the runtime the self-contained build and the launcher carry is that patch's. To run a build from
its output folder as administrator (CI does), start `app\PoolWatch.exe` itself from an administrator console with
`--allow-any-location` (the launcher starts nothing outside Program Files); never use that for a copy you keep. CI's
UI test (`--ui-smoke`) and the token-leak test's hidden helper skip the place check too.

The CI (`.github/workflows/ci.yml`) runs on every pull request and push to `main`: the unit tests (also on Linux),
the native self-test, the CSV checks, end-to-end leak scenarios with `tools/Leaker`, thirteen diagnostics scenarios,
the installer (install, a real change, uninstall, checks), and the UI test that takes the README's screenshots. A
45-minute soak under a busy-PC load and an overhead check run on demand and before every release; PoolWatch's budget,
judged on the CI runner, is under 0.5% of one core and a working set under 100 MB. Pull requests also get a dependency
review, and the repository has code scanning, secret scanning and Dependabot turned on. A `v*` tag builds, signs,
verifies and publishes the release.
