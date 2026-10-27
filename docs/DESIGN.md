# PoolWatch design

How PoolWatch works and why it is built the way it is. The code refers to these sections by number
("DESIGN.md section 7"). What PoolWatch changes on your PC, and how each change is confirmed, is in
[SECURITY.md](../SECURITY.md).

## 1. Why it exists

On a developer PC with a ReFS Dev Drive, the kernel pool grew to about 23 GB (the Task Manager figure) and the
machine slowed, typing included. Working it out by hand took this loop: sample the pool tags, guess which process
was busy, then search AI coding-agent transcripts to find which session had issued the command. It found, in order:

- a desktop app churning its GPU shader disk cache on the Dev Drive;
- an orphaned recursive `find` (`find / -maxdepth 6 -type d -name ...`, started by an agent's tool call that timed
  out, its parent shell gone) crawling every drive for 90 minutes; the pool climbed about 400 MB a minute while it
  ran and stopped growing when it was killed;
- the memory came back only when stale git worktrees were deleted: the pool was holding per-file and per-directory
  file-system metadata for files that had been touched.

The slower, everyday version is a pool that passes 10 GB within a day and forces a restart. PoolWatch automates the
loop above, for the slow climb as well as the sudden burst.

Tags that matter here (from Microsoft's `pooltag.txt`; tags are **case-sensitive**: `Reff` and `RefF` differ). The
built-in table (`src/PoolWatch.Core/Tags/BuiltinTags.cs`) holds these and common NTFS and filter-manager tags:

| Tag | Owner | Meaning |
|---|---|---|
| `FMfn` | fltmgr.sys | NAME_CACHE_NODE (filter manager file-name cache) |
| `File` | nt | File objects (charged to the opening process) |
| `Reff` | refs.sys | FCB_DATA (per-file control block) |
| `RefF` | refs.sys | FCB_INDEX (per-directory control block) |
| `Refx` | refs.sys | General allocation |
| `RLin` | FsLib | Range lock entry |
| `MSPi` `MSeb` `MSb+` `MSde` `MSpa` | refs.sys | Minstore (ReFS metadata store) structures |
| `MmSt` | nt!mm | Section object prototype PTEs (grows with mapped / cached files) |
| `CcVl` | nt!cc | Cache Manager VACB level structures |
| `Nbuf` | netio.sys | NetIO MDL allocations |
| `EtwB` | nt!etw | ETW buffers |
| `AfdB` | afd.sys | AFD data buffer |
| `Toke` | nt!se | Token objects (the token-leak tag of some Windows 11 Insider builds) |

## 2. Goals and non-goals

1. Continuously and cheaply record pool totals, per-tag usage and per-process activity.
2. Detect abnormal growth, retention and release, per tag.
3. Correlate growth with processes, services, handles and the process lifecycle (start, exit, orphaning), and rank
   suspects with plain-English evidence and a confidence level.
4. On demand or on alert, run a short confirmation trace that attributes allocations to process, driver and stack.
5. Write an incident folder with a bug-report draft: the process, its command line, parent chain, start time, the
   tag, the owning driver, the timeline and the evidence.
6. Optionally, understand AI Code Conductor's process tree and Claude Code sessions well enough to say which session
   (project, account profile, workflow, sub-agent) started an offending process.

Non-goals: no kernel driver; no third-party monitoring tools (PoolMon, RAMMap and the like are neither shipped nor
required); no network calls or telemetry. PoolWatch reads by default; every change it can make to the system is
listed in SECURITY.md and needs your action (or a setting you turn on).

## 3. Hard constraints

- **Overhead budget** (steady state on a busy machine, measured by PoolWatch itself and shown in About and every
  report): under 0.5% of one core on average, under 100 MB working set, under 5 MB an hour of disk writes, and its own
  handles and private memory flat over hours. A confirmation trace may exceed it for its bounded length only.
- **Where it writes**: only under its admin-only data folder `%ProgramData%\PoolWatch` (or an explicit headless
  `--out` folder), never next to the exe, never through a link, and never on a ReFS volume (every Dev Drive is ReFS:
  its per-file metadata is the pool being measured). 7-day retention by default.
- **No WMI in the sampling path** (it is expensive). Native APIs only.
- **PoolWatch must not become a crawler itself.** Every file-system lookup is bounded: explicit paths,
  modified-within windows, file and byte budgets, early exit.
- **Runs elevated** (manifest `requireAdministrator`), and therefore only from an administrator-only folder such as
  `%ProgramFiles%\PoolWatch`. An optional "start at logon" scheduled task with highest privileges avoids the daily UAC
  prompt; it is only ever created for an installed copy.
- Windows 10 22H2+ / Windows 11, x64. .NET 10 (`net10.0-windows`, WinForms): a framework-dependent build and a
  self-contained build.

## 4. Collectors

| What | API | Interval |
|---|---|---|
| Per-tag pool usage, alloc / free counts | `NtQuerySystemInformation(SystemPoolTagInformation = 22)` | 10 s |
| Committed pool totals (what Task Manager shows) | PDH `\Memory\Pool Paged Bytes`, `\Memory\Pool Nonpaged Bytes` | 10 s |
| All processes: PID, parent, create time, image, handles, threads, quota pool, I/O counts, private bytes | `NtQuerySystemInformation(SystemProcessInformation = 5)`, one call | 10 s |
| Process start / stop, short-lived ones included: image, command line, parent, user, exit code | real-time ETW session, `Microsoft-Windows-Kernel-Process` (TraceEvent) | events |
| Services hosted per PID | `EnumServicesStatusEx` with `SERVICE_STATUS_PROCESS` | 60 s and on svchost start |
| Handles per process by object type | `NtQuerySystemInformation(SystemExtendedHandleInformation = 64)` + `NtQueryObject(ObjectTypesInformation)` | 5 min, on alert and on Capture now; a scan over 250 ms skips the next |
| Volumes: file system, Dev Drive flag | `GetVolumeInformation`, `FSCTL_QUERY_PERSISTENT_VOLUME_STATE` | start and hourly |

Notes:
- `SystemPoolTagInformation` on x64: `ULONG Count`, then 40-byte entries from offset 8: `Tag[4]` @0, `PagedAllocs` @4,
  `PagedFrees` @8, `PagedUsed` (SIZE_T) @16, `NonPagedAllocs` @24, `NonPagedFrees` @28, `NonPagedUsed` (SIZE_T) @32.
  Tags are keyed by their raw 4 bytes, compared ordinally.
- A process is identified by (PID, create time): PIDs are reused.
- The tag sum runs lower than the committed-pages total (fragmentation, lazy page release). Both are shown and labelled.

## 5. Tag knowledge

For every tag shown, owner and description come from, in order:
1. `pooltag.txt` from the Debugging Tools for Windows, when installed (read at run time, never shipped; at most 8 MB
   of it is read);
2. a scan of the loaded drivers and `%SystemRoot%\System32\drivers\*.sys` for the 4 tag bytes (the classic
   `findstr /m /l` method), cached;
3. the built-in table (section 1).

Object-manager tags map to handle types (`File`, `Even`, `Toke`, `Key `, `Sect` / `MmSt`, `Thre`, `Proc`, `Muta`, ...),
which powers direct attribution.

## 6. Detection

Per tag, over 1-minute, 10-minute, 60-minute and since-boot windows. "What has grown since boot, and what drove it,
hour by hour" is a first-class report (Since boot), from one-minute history.

- **Growth**: by default 200 MB in 10 min, 1 GB in 60 min, or total non-paged pool over 5 GB and still rising (all
  configurable).
- **Retention**: the tag grew during an activity burst and has not fallen within 15 minutes (default) after the
  correlated activity stopped.
- **Release**: a sharp drop. What happened at that moment (process exits, mass deletes) is recorded: it explains what
  was holding the memory. The busiest processes are counted from the sample before the drop began. I/O counters are
  read only at samples, and an exit does not report them, so a process that deletes and exits between two samples is
  counted over the release window up to its last sample instead, and marked as exited with its start time.
- Change points (growth onset and end) feed the correlation.

## 7. Correlation

For each alert, suspects (processes; svchost with its services) are ranked on every signal, each with its evidence:

1. **Direct charge**: the process's quota pool rose, and its handles of the matching object type rose.
2. **Activity correlation**: Spearman rank correlation (plus a lagged version) between the tag's growth and each
   process's I/O rate, and its share of all I/O in the window.
3. **Lifecycle**: started just before onset, exited when growth stopped, or orphaned (parent gone) while busy.
4. **Ancestry**: the full parent chain, with dead ancestors recorded from the ETW start events.
5. **Confidence**: *correlated*, *strongly correlated* (several signals agree) or *confirmed by trace* (section 8).
   PoolWatch never states causation from correlation alone.

## 8. Confirmation trace

Started by Capture now, or by an alert when `CaptureOnAlert` is on (rate-limited: at most one automatic capture an
hour, never while the PC is saturated or in use). Default 60 s, hard cap 5 minutes, one at a time, with the in-box
`wpr.exe` and a generated profile:

- kernel pool allocation and free events with stacks, filtered to the alerting tags, plus process, thread and image
  events;
- optionally `Microsoft-Windows-Kernel-File`, to attribute file churn per process and volume, and a CPU trace with
  stacks (off by default: it lags a busy PC itself);
- the `.etl` is kept in the incident folder and parsed into net bytes per process, tag and driver frame, and top
  directories per process. Kernel functions are named only from local, admin-only symbol folders; no symbol server
  is contacted.

File-system allocations made on worker threads show as System (PID 4); the stacks and the file activity tie them back
to the requesting process.

## 9. AI Code Conductor and Claude Code awareness (optional)

Generic Electron / Chromium understanding first: children are labelled by `--type=` (`renderer`, `gpu-process`,
`utility` plus `--utility-sub-type=`) for any Electron app.

AI Code Conductor (AICC) specifics, used only when it is running:
- the main exe `AI Code Conductor.exe`; its `node.mojom.NodeService` utility processes are background workers (the one
  with by far the most reads is labelled the Tokenomics worker);
- PTY hosts (`conhost.exe`, `OpenConsole.exe`) under AICC, and the session CLIs below them, are "AICC session"
  processes;
- for each session CLI the working directory and environment are read from the process (PEB); the account profile
  comes from `CLAUDE_CONFIG_DIR` / `USERPROFILE` (`...\account-profiles\profile-<id>`), shown as
  "AICC › D:\sample-app (profile-xxxx) › bash › find.exe".

Which Claude Code session issued a command: for a suspect whose ancestry contains `claude.exe`, the transcripts under
`<config dir>\projects\*\*.jsonl` and `...\subagents\**\*.jsonl`, modified within 30 minutes of the process start, are
searched for the command's distinctive core, reading only tool-call lines. The result is the session id, project
folder, workflow and sub-agent, and timestamp. The config dirs searched are the session's own, every profile of the
AICC install it belongs to (or of the `AiccResourcesRoot` setting), and `%USERPROFILE%\.claude`. Bounded: a cap on
files and bytes, and it stops at the first exact hit.

Git worktree sprawl is flagged as context, not as a crawl: when a release coincides with many deletions under
`.git\worktrees` or a worktree folder, the incident says so.

## 10. Incidents, bug reports and redaction

Each alert or capture creates `incidents\<yyyyMMdd-HHmmss>-<tag>-<raw hex>\` with:
- `report.md`: headline, timeline chart, ranked suspects with evidence and confidence, process details (path,
  version, signer, command line, parent chain, services, start and exit times, AICC / Claude Code session), system
  info (OS build, RAM, volumes with file system and Dev Drive flag, filter drivers) and a draft bug report for the
  owning vendor;
- `report.json`, `timeline.csv`, `processes.csv`, `events.csv`, `timeline.png`, and the `.etl` when a trace was taken.

These files are written as recorded, for you, and each report says so at the top ("NOT REDACTED"). **Export report**
builds a zip for sharing, redacted by default: the user name, home and TEMP paths (long and 8.3 forms), machine name,
domain and SID are replaced by placeholders; values that look like secrets (a password, token or API key on a command
line, an Authorization header, credentials in a URL, well-known token shapes) become `<SECRET>`; and the `.etl` (which
cannot be redacted reliably) is left out. The vendor bundle and copied text (`RedactCopies`, on by default) use the
same redaction. It is best-effort: read an export before you post it.

## 11. UI

Tray icon coloured by state (green, amber, red), with the paged and non-paged totals in its tooltip. The main window
has the pool chart (both measures, with incident markers), the growing-tags table, and per tag its suspects, evidence
and events; tabs for Incidents, Processes, Findings, Background services and Settings. It renders only while visible:
no timers run while it is hidden.

## 12. Build and test

The code builds anywhere the .NET 10 SDK runs (`EnableWindowsTargeting`); it runs only on Windows.

- Unit tests (xUnit, `tests/PoolWatch.Tests`): parsing against byte fixtures, tag lookup, detection on synthetic
  series, ranking, transcript lookup on fixture files, redaction. They also run on Linux.
- App-layer tests (`tests/PoolWatch.AppTests`, Windows only): every system change goes through an injected fake; no
  test changes a service, task, registry value or ACL, or ends a process.
- End-to-end on the Windows CI runner (elevated): PoolWatch runs headless while `tools/Leaker` reproduces a pattern
  (a File-handle leak, file churn with release, an orphaned child, the confirmation trace, spawn storms and more), and
  the generated reports are checked. A UI smoke test takes the screenshots; a soak checks the overhead budget under a
  busy-PC load.
