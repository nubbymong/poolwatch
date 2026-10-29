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
with administrator rights, without the prompt.

What PoolWatch does with those rights. The first six happen on their own; the others only when you choose them, with
a click or a setting you turned on:

| What | When |
|---|---|
| Reads the pool tags, every process and its handles, and watches program starts | always, while it runs |
| Creates its own data folder, `C:\ProgramData\PoolWatch`, that only administrators can change, and moves aside one that someone else owns or can change, or a link | at every start |
| Removes the unused clear-cache tasks and helper files a pre-release PoolWatch installed | once, when it finds them |
| Moves a pre-release PoolWatch's own files from `%LOCALAPPDATA%\PoolWatch` into its data folder: each is copied and checked, then deleted there | once, after an upgrade from such a version (tried again at the next starts if it could not finish) |
| Checks the task that starts PoolWatch at sign-in; if it starts something outside Program Files, or something PoolWatch cannot check, PoolWatch asks whether to point it at the installed PoolWatch or remove it, and changes it only on your answer | at every start |
| Checks whether this Windows build has the token leak: starts a tiny Windows command 600 times in the background and measures, usually about 20 seconds; nothing is changed | by itself when setup reads the PC on Windows 11 24H2 and later, after a Windows update while the token-leak guard is off (the token-leak watch, once setup was completed), and when the guard starts |
| Clears the standby cache (no file and no program is touched) | **Clear now**, the automatic clear, or the "lag before / after a clear" experiment you start |
| Records a trace of up to 5 minutes with Windows' own `wpr.exe` | **Capture now**, or when an incident opens if you turn that setting on (off by default) |
| Turns background services, scheduled tasks or startup apps off or back on | your click on the Services tab or the Tasks and startup apps tab, **Apply** in the review of an AI agent's answer, or **Put back** |
| Turns off the services of your service profile that are on (never a protected service, and never one PoolWatch does not recognise) | each time PoolWatch starts, after the 1 to 15 minutes you chose, if you ticked **Apply this profile when PoolWatch starts** for that profile |
| Ends a program (**End it now**) | your click, after a question that names it |
| Creates the scheduled task that starts it at sign-in | your switch in setup, or your click in Settings |
| Replaces your own RAMMap clear task, or puts it back | your choice of a rule in setup's Standby cache step (it says so), Settings, or **Put back** |
| Sets Windows' focus setting that stops the token leak, until you sign out | the token-leak guard, when you turn it on |
| Creates the desktop shortcut "ChatGPT (no shader cache)", the Codex shortcut | your Yes in setup, or **Create shortcut** in Settings |
| Writes the AI agent's prompt, `poolwatch-services.md` | **Save the prompt...**, only where you pick |

Because it runs as administrator, it runs only from inside Program Files, and refuses a copy there whose folders or
file you could change without administrator rights (anywhere else, such as Downloads or the Desktop, it refuses
outright). Only the switches its tests use skip that check (see [Build from source](#build-from-source)). It never
writes through a file-system link (a junction or symbolic link) and never installs a driver.
[SECURITY.md](../SECURITY.md) lists every action and how each is checked, and its
[known limitations](../SECURITY.md#known-limitations), one of which (.NET start-up variables) has no defence yet in
1.0.0.

## How it decides

Per pool tag it looks for **growth** (by default 200 MB in 10 minutes, 1 GB in 60 minutes, or non-paged pool over
5 GB and still rising), **retention** (still held 15 minutes after the activity stopped) and **release** (a sharp drop:
whatever exited or was deleted then was holding the memory). Suspects are ranked on direct charge (handles of the
matching type, quota pool), activity correlation (Spearman, with a lag), lifecycle (started at onset, exited at the
end, orphaned) and ancestry. Labels are *correlated*, *strongly correlated* or *confirmed by trace*; PoolWatch never
claims causation from correlation alone. What the pool does from just before PoolWatch's own cache clear until the
clear is measured 2 minutes later is that clear's result, never a release or a fall. Growth is measured across
PoolWatch's clears on one baseline: what a clear freed is added back, so growth after a clear counts from the level it
left and growth before it still counts ([DESIGN.md, section 6](DESIGN.md#6-detection)). So a leak just over a growth
rule that the clears keep trimming can go unreported: at the default 10-second sampling up to about 4% over the
hourly rule with hourly clears, and more at a longer sampling interval. A release is raised across a clear only on
what the tag fell after the clear was measured. Section 6 has every limit.

The growth and clear stats (the Simple view's tiles and the Advanced view's panel) count on the same baseline: each
minute keeps the pool's change with what the clears freed added back, and what they freed, for 25 hours, in memory only.
A window adds its minutes up before it says Grew or Fell on its own, so a pool that only wobbles shows neither. A change
across a sleep, a stall or a clock set back is not counted, and that time is not measured, but a drop across a clear's
interval is still that clear's, unless the clock is set back while the clear is being measured. The rate is that change
per hour measured, shown after 30 minutes. A clear whose purge fails only after its 2-minute measurement still counts as
cleared there ([DESIGN.md, section 11](DESIGN.md#11-ui)).

**Impact levels** (setup's "How your PC is impacted" and the Impact column in Pool causes) come from one rule
(`src/PoolWatch.Core/Impact/ImpactRules.cs`). Measured: the growth per hour with PoolWatch's own clears added back
(never the net change, which the clears keep small), over the window shown: None under 10 MB an hour, Low under 5 GB a
day, Medium under 10 GB a day, High from 10 GB a day; a fall is None, and there is no level until 30 minutes are
measured. GB is 2^30 bytes, as PoolWatch shows sizes. Before anything is measured (a first run), setup gives each known
cause a level from what it found: a Dev Drive or another ReFS drive, High; Microsoft Defender's real-time protection
with a Dev Drive that performance mode does not cover, Medium, and with no Dev Drive at all, Low; the Codex desktop
app installed, Low; services PoolWatch knows running, Low; the token leak found by PoolWatch's check, High. Anything
PoolWatch could not read counts as not found. What PoolWatch measures for a cause wins over what it found.

**Costs on the Services and Tasks tabs** use the same four words and the same meter for something else: what a
service's or a program's processes cost the processor now (`EntryCost`). For a startup app or a task, "now" is the
latest sampling window; for an app busy in the background, its 30-minute average.

When a suspect runs under Claude Code, PoolWatch searches that session's transcripts on this PC (bounded) for the tool
call that started it, and names the session, project, workflow and sub-agent; under AI Code Conductor (an app that
runs several Claude Code sessions) it also names the session and its account profile. PoolWatch never sends any of it
(see [Privacy](../README.md#privacy)); an export keeps it, so remove it before you share one if it is private.
[An example incident report](example-incident/report.md), from a CI run of an earlier version (before reports
said "NOT REDACTED" at the top).

## Where it stores data

Everything goes under `C:\ProgramData\PoolWatch\` (administrators can change it; your account can read it), kept
7 days by default:

- `data\<yyyyMMdd>\`: the CSV files (totals, tags, processes, handles, events, services, volumes, lag, load) and
  `findings.jsonl`, about 2-3 MB an hour (about 350-500 MB over the 7 days kept);
- `incidents\<id>\`: `report.md`, `report.json`, `timeline.csv`, `processes.csv`, `events.csv`, `timeline.png`, and
  `capture.etl` when a trace was taken (a trace can add far more than the CSV files);
- `settings.json` (your settings and service profiles), `poolwatch.log`, `service-changes.json` and
  `applied-changes.json` (what Active changes needs to put a change back; the file keeps its older name, so an
  installed copy's records stay readable), `removed-tasks\`, `setup.json` (when setup was completed, and the
  suggestions and feature cards you hid), `ui-state.json` (Simple or Advanced view), `uninstall-summary.txt`,
  `uninstall-list.json` (only while the uninstaller runs), `boots\`, `cache\`, `exports\`, `reports\`, `requests\`
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
PoolWatch.exe --stop-running        ask a running PoolWatch to exit (exit code 7: still running)
PoolWatch.exe --selftest --out <dir>   native collector checks against independent sources
```

Any unknown option (for example `--help`) prints the full list, run from an administrator console.

`--changes-dump` writes the Active changes list as JSON, one object per row, in camel case: among others `what` (the
change in one line), `title` and `detail` (as the window shows them), `group`, `canUndo` and `note`, `uninstallLeaves`
(why the uninstaller leaves it, when it does) and `putBackCaveat` (what stays for a while after it is put back: today
only the token-leak guard's setting, until sign-out). Names are written as they are. The uninstaller writes this list
as `uninstall-list.json` in the data folder, reads `what`, `uninstallLeaves` and `putBackCaveat` from it line by line,
and deletes it.

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
dotnet publish src/PoolWatch/PoolWatch.csproj -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/sc
```

Package versions are locked (`packages.lock.json`); CI restores in locked mode. To run a build from its output folder
as administrator (CI does), add `--allow-any-location`; never use that for a copy you keep. CI's UI test
(`--ui-smoke`) and the token-leak test's hidden helper skip the place check too.

The CI (`.github/workflows/ci.yml`) runs on every pull request and push to `main`: the unit tests (also on Linux),
the native self-test, the CSV checks, end-to-end leak scenarios with `tools/Leaker`, thirteen diagnostics scenarios,
the installer (install, a real change, uninstall, checks), and the UI test that takes the README's screenshots. A
45-minute soak under a busy-PC load and an overhead check run on demand and before every release; PoolWatch's budget,
judged on the CI runner, is under 0.5% of one core and a working set under 100 MB. Pull requests also get a dependency
review, and the repository has code scanning, secret scanning and Dependabot turned on. A `v*` tag builds, signs,
verifies and publishes the release.
